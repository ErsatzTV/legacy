using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Plex;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ErsatzTV.Infrastructure.Streaming;

public class ExternalJsonPlayoutItemProvider : IExternalJsonPlayoutItemProvider
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalStatisticsProvider _localStatisticsProvider;
    private readonly ILogger<ExternalJsonPlayoutItemProvider> _logger;
    private readonly IPlexPathReplacementService _plexPathReplacementService;
    private readonly IPlexSecretStore _plexSecretStore;
    private readonly IPlexServerApiClient _plexServerApiClient;

    public ExternalJsonPlayoutItemProvider(
        IDbContextFactory<TvContext> dbContextFactory,
        IFileSystem fileSystem,
        IPlexPathReplacementService plexPathReplacementService,
        IPlexServerApiClient plexServerApiClient,
        IPlexSecretStore plexSecretStore,
        ILocalStatisticsProvider localStatisticsProvider,
        ILogger<ExternalJsonPlayoutItemProvider> logger)
    {
        _dbContextFactory = dbContextFactory;
        _fileSystem = fileSystem;
        _plexPathReplacementService = plexPathReplacementService;
        _plexServerApiClient = plexServerApiClient;
        _plexSecretStore = plexSecretStore;
        _localStatisticsProvider = localStatisticsProvider;
        _logger = logger;
    }

    public async Task<Either<BaseError, PlayoutItemWithPath>> CheckForExternalJson(
        Channel channel,
        DateTimeOffset now,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Playout> maybePlayout = await dbContext.Playouts
            .AsNoTracking()
            .SelectOneAsync(p => p.ChannelId, p => p.ChannelId == channel.Id, cancellationToken);

        foreach (Playout playout in maybePlayout)
        {
            // playout must be external json
            if (playout.ScheduleKind == PlayoutScheduleKind.ExternalJson)
            {
                // json file must exist
                if (_fileSystem.File.Exists(playout.ScheduleFile))
                {
                    return await GetExternalJsonPlayoutItem(dbContext, playout, now, ffprobePath, cancellationToken);
                }

                _logger.LogWarning(
                    "Unable to locate json schedule file {File} for channel {Number} - {Name}",
                    playout.ScheduleFile,
                    channel.Number,
                    channel.Name);
            }
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> GetExternalJsonPlayoutItem(
        TvContext dbContext,
        Playout playout,
        DateTimeOffset now,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        Option<ExternalJsonChannel> maybeChannel = JsonConvert.DeserializeObject<ExternalJsonChannel>(
            await _fileSystem.File.ReadAllTextAsync(playout.ScheduleFile, cancellationToken));

        // must deserialize channel from json
        foreach (ExternalJsonChannel channel in maybeChannel)
        {
            if (string.IsNullOrWhiteSpace(channel.StartTime))
            {
                _logger.LogError(
                    "External json channel in file {File} has no start time; unable to locate playout item",
                    playout.ScheduleFile);

                throw new InvalidOperationException(
                    $"External json channel in file {playout.ScheduleFile} has no start time");
            }

            DateTimeOffset startTime = DateTimeOffset.Parse(
                channel.StartTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal).ToLocalTime();

            //_logger.LogDebug("external json start time: {StartTime}", startTime);

            foreach (ExternalJsonProgram program in channel.Programs)
            {
                int milliseconds = program.Duration;
                DateTimeOffset nextStart = startTime + TimeSpan.FromMilliseconds(milliseconds);
                if (nextStart > now)
                {
                    //_logger.LogDebug("should play program {@Program}", program);
                    return await BuildPlayoutItem(dbContext, startTime, program, ffprobePath, cancellationToken);
                }

                startTime = nextStart;
            }
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> BuildPlayoutItem(
        TvContext dbContext,
        DateTimeOffset startTime,
        ExternalJsonProgram program,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        // find any library path from the appropriate plex server
        List<LibraryPath> maybeLibraryPath = await dbContext.LibraryPaths
            .AsNoTracking()
            .Filter(lp => ((PlexMediaSource)((PlexLibrary)lp.Library).MediaSource).ServerName == program.ServerKey)
            .OrderBy(lp => lp.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        foreach (LibraryPath libraryPath in maybeLibraryPath.HeadOrNone())
        {
            string localPath = await _plexPathReplacementService.GetReplacementPlexPath(
                libraryPath.Id,
                program.File,
                cancellationToken);

            if (_fileSystem.File.Exists(localPath))
            {
                return await StreamLocally(startTime, program, ffprobePath, localPath);
            }

            return await StreamRemotely(dbContext, startTime, program, cancellationToken);
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> StreamLocally(
        DateTimeOffset startTime,
        ExternalJsonProgram program,
        string ffprobePath,
        string localPath)
    {
        // ffprobe on demand
        Either<BaseError, MediaVersion> maybeMediaVersion =
            await _localStatisticsProvider.GetStatistics(ffprobePath, localPath);

        foreach (MediaVersion mediaVersion in maybeMediaVersion.RightToSeq())
        {
            // build playout item
            var episode = new Episode
            {
                MediaVersions = [mediaVersion],
                EpisodeMetadata =
                [
                    new EpisodeMetadata
                    {
                        EpisodeNumber = program.Episode,
                        Title = program.Title
                    }
                ],
                Season = new Season
                {
                    SeasonNumber = program.Season,
                    Show = new Show
                    {
                        ShowMetadata =
                        [
                            new ShowMetadata
                            {
                                Title = program.ShowTitle
                            }
                        ]
                    }
                }
            };

            return new PlayoutItemWithPath(GetPlayoutItem(startTime, episode, program), localPath);
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> StreamRemotely(
        TvContext dbContext,
        DateTimeOffset startTime,
        ExternalJsonProgram program,
        CancellationToken cancellationToken)
    {
        Option<PlexMediaSource> maybeServer = await dbContext.PlexMediaSources
            .Include(pms => pms.Connections)
            .SelectOneAsync(pms => pms.ServerName, pms => pms.ServerName == program.ServerKey, cancellationToken);

        if (maybeServer.IsNone)
        {
            _logger.LogWarning(
                "Unable to stream remotely; no Plex server found with server name {ServerName}",
                program.ServerKey);
        }

        foreach (PlexMediaSource server in maybeServer)
        {
            Option<PlexConnection> maybeConnection = server.Connections.SingleOrDefault(c => c.IsActive);
            if (maybeConnection.IsNone)
            {
                _logger.LogWarning(
                    "Unable to stream remotely; Plex server {ServerName} has no active connection",
                    server.ServerName);
            }

            foreach (PlexConnection connection in maybeConnection)
            {
                Option<PlexServerAuthToken> maybeToken =
                    await _plexSecretStore.GetServerAuthToken(server.ClientIdentifier);

                if (maybeToken.IsNone)
                {
                    _logger.LogWarning(
                        "Unable to stream remotely; Plex server {ServerName} has no auth token",
                        server.ServerName);
                }

                foreach (PlexServerAuthToken token in maybeToken)
                {
                    MediaItem mediaItem = program.Type switch
                    {
                        "episode" => await GetPlexEpisode(server, connection, token, program),
                        _ => await GetPlexMovie(server, connection, token, program)
                    };

                    return new PlayoutItemWithPath(
                        GetPlayoutItem(startTime, mediaItem, program),
                        $"http://localhost:{Settings.StreamingPort}/media/plex/{server.Id}/{program.PlexFile}");
                }
            }
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<MediaItem> GetPlexEpisode(
        PlexMediaSource plexMediaSource,
        PlexConnection connection,
        PlexServerAuthToken token,
        ExternalJsonProgram program)
    {
        Either<BaseError, Tuple<EpisodeMetadata, MediaVersion>> maybeStatistics =
            await _plexServerApiClient.GetEpisodeMetadataAndStatistics(
                plexMediaSource.Id,
                program.RatingKey,
                connection,
                token);

        foreach (Tuple<EpisodeMetadata, MediaVersion> result in maybeStatistics.RightToSeq())
        {
            return new PlexEpisode
            {
                EpisodeMetadata = [result.Item1],
                MediaVersions = [result.Item2]
            };
        }

        throw new NotSupportedException();
    }

    private async Task<MediaItem> GetPlexMovie(
        PlexMediaSource plexMediaSource,
        PlexConnection connection,
        PlexServerAuthToken token,
        ExternalJsonProgram program)
    {
        Either<BaseError, Tuple<MovieMetadata, MediaVersion>> maybeStatistics =
            await _plexServerApiClient.GetMovieMetadataAndStatistics(
                plexMediaSource.Id,
                program.RatingKey,
                connection,
                token);

        foreach (Tuple<MovieMetadata, MediaVersion> result in maybeStatistics.RightToSeq())
        {
            return new PlexMovie
            {
                MovieMetadata = [result.Item1],
                MediaVersions = [result.Item2]
            };
        }

        throw new NotSupportedException();
    }

    private static PlayoutItem GetPlayoutItem(
        DateTimeOffset startTime,
        MediaItem mediaItem,
        ExternalJsonProgram program) =>
        new()
        {
            Start = startTime.UtcDateTime,
            Finish = startTime.AddMilliseconds(program.Duration).UtcDateTime,
            FillerKind = FillerKind.None,
            ChapterTitle = null,
            GuideFinish = null,
            GuideGroup = 0,
            CustomTitle = null,
            InPoint = TimeSpan.Zero,
            OutPoint = TimeSpan.FromMilliseconds(program.Duration),
            MediaItem = mediaItem
        };
}
