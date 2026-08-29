using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Security;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Subtitles.Queries;

public class GetSubtitlePathByIdHandler(IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<GetSubtitlePathById, Either<BaseError, SubtitlePathAndCodec>>
{
    public async Task<Either<BaseError, SubtitlePathAndCodec>> Handle(
        GetSubtitlePathById request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<Subtitle> maybeSubtitle = await dbContext.Subtitles
            .AsNoTracking()
            .SelectOneAsync(s => s.Id, s => s.Id == request.Id, cancellationToken);

        foreach (var subtitle in maybeSubtitle)
        {
            if (subtitle is { SubtitleKind: SubtitleKind.Embedded, IsExtracted: true })
            {
                string path = Path.Combine(FileSystemLayout.SubtitleCacheFolder, subtitle.Path);
                return new SubtitlePathAndCodec(path, subtitle.Codec);
            }

            foreach (string plexUrl in await GetPlexUrl(request.Id, dbContext, maybeSubtitle))
            {
                return new SubtitlePathAndCodec(plexUrl, subtitle.Codec);
            }

            foreach (string jellyfinUrl in await GetJellyfinUrl(request.Id, dbContext, maybeSubtitle))
            {
                return new SubtitlePathAndCodec(jellyfinUrl, subtitle.Codec);
            }

            foreach (string embyUrl in await GetEmbyUrl(request.Id, dbContext, maybeSubtitle))
            {
                return new SubtitlePathAndCodec(embyUrl, subtitle.Codec);
            }

            return new SubtitlePathAndCodec(subtitle.Path, subtitle.Codec);
        }

        return BaseError.New($"Unable to locate subtitle with id {request.Id}");
    }

    protected static async Task<Option<string>> GetPlexUrl(
        int subtitleId,
        TvContext dbContext,
        Option<Subtitle> maybeSubtitle)
    {
        // check for plex episode
        Option<int> maybePlexId = await dbContext.Connection.QuerySingleOrDefaultAsync<int?>(
                @"select PMS.Id from PlexMediaSource PMS
                     inner join Library L on PMS.Id = L.MediaSourceId
                     inner join LibraryPath LP on L.Id = LP.LibraryId
                     inner join MediaItem MI on LP.Id = MI.LibraryPathId
                     inner join EpisodeMetadata EM on EM.EpisodeId = MI.Id
                     inner join Subtitle S on EM.Id = S.EpisodeMetadataId
                     where S.Id = @SubtitleId",
                new { SubtitleId = subtitleId })
            .Map(Optional);

        // check for plex movie
        if (maybePlexId.IsNone)
        {
            maybePlexId = await dbContext.Connection.QuerySingleOrDefaultAsync<int?>(
                    @"select PMS.Id from PlexMediaSource PMS
                     inner join Library L on PMS.Id = L.MediaSourceId
                     inner join LibraryPath LP on L.Id = LP.LibraryId
                     inner join MediaItem MI on LP.Id = MI.LibraryPathId
                     inner join MovieMetadata MM on MM.MovieId = MI.Id
                     inner join Subtitle S on MM.Id = S.MovieMetadataId
                     where S.Id = @SubtitleId",
                    new { SubtitleId = subtitleId })
                .Map(Optional);
        }

        foreach (int plexMediaSourceId in maybePlexId)
        {
            foreach (string subtitlePath in maybeSubtitle.Map(s => s.Path))
            {
                DateTimeOffset exp = DateTimeOffset.Now + TimeSpan.FromHours(1);

                string sig = InternalUrlSigner.Sign(
                    exp,
                    "plex",
                    $"{plexMediaSourceId}",
                    subtitlePath);

                return $"http://localhost:{Settings.StreamingPort}/internal/media/plex/{plexMediaSourceId}/{subtitlePath}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
            }
        }

        return Option<string>.None;
    }

    protected static async Task<Option<string>> GetJellyfinUrl(
        int subtitleId,
        TvContext dbContext,
        Option<Subtitle> maybeSubtitle)
    {
        // check for jellyfin episode
        Option<string> maybeJellyfinId = await dbContext.Connection.QuerySingleOrDefaultAsync<string>(
                @"select JE.ItemId from JellyfinEpisode JE
                     inner join EpisodeMetadata EM on EM.EpisodeId = JE.Id
                     inner join Subtitle S on EM.Id = S.EpisodeMetadataId
                     where S.Id = @SubtitleId",
                new { SubtitleId = subtitleId })
            .Map(Optional);

        // check for jellyfin movie
        if (maybeJellyfinId.IsNone)
        {
            maybeJellyfinId = await dbContext.Connection.QuerySingleOrDefaultAsync<string>(
                    @"select JM.ItemId from JellyfinMovie JM
                     inner join MovieMetadata MM on MM.MovieId = JM.Id
                     inner join Subtitle S on MM.Id = S.MovieMetadataId
                     where S.Id = @SubtitleId",
                    new { SubtitleId = subtitleId })
                .Map(Optional);
        }

        foreach (string jellyfinItemId in maybeJellyfinId)
        {
            foreach (Subtitle subtitle in maybeSubtitle)
            {
                int index = subtitle.StreamIndex - JellyfinStream.ExternalStreamOffset;
                string extension = Subtitle.ExtensionForCodec(subtitle.Codec);
                var subtitlePath =
                    $"Videos/{jellyfinItemId}/{jellyfinItemId}/Subtitles/{index}/{index}/Stream.{extension}";

                DateTimeOffset exp = DateTimeOffset.Now + TimeSpan.FromHours(1);

                string sig = InternalUrlSigner.Sign(exp, "jellyfin", subtitlePath);

                return $"http://localhost:{Settings.StreamingPort}/internal/media/jellyfin/{subtitlePath}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
            }
        }

        return Option<string>.None;
    }

    protected static async Task<Option<string>> GetEmbyUrl(
        int subtitleId,
        TvContext dbContext,
        Option<Subtitle> maybeSubtitle)
    {
        // check for emby episode
        Option<string> maybeEmbyId = await dbContext.Connection.QuerySingleOrDefaultAsync<string>(
                @"select EE.ItemId from EmbyEpisode EE
                     inner join EpisodeMetadata EM on EM.EpisodeId = EE.Id
                     inner join Subtitle S on EM.Id = S.EpisodeMetadataId
                     where S.Id = @SubtitleId",
                new { SubtitleId = subtitleId })
            .Map(Optional);

        // check for emby movie
        if (maybeEmbyId.IsNone)
        {
            maybeEmbyId = await dbContext.Connection.QuerySingleOrDefaultAsync<string>(
                    @"select EM.ItemId from EmbyMovie EM
                     inner join MovieMetadata MM on MM.MovieId = EM.Id
                     inner join Subtitle S on MM.Id = S.MovieMetadataId
                     where S.Id = @SubtitleId",
                    new { SubtitleId = subtitleId })
                .Map(Optional);
        }

        foreach (string embyItemId in maybeEmbyId)
        {
            foreach (Subtitle subtitle in maybeSubtitle)
            {
                string extension = Subtitle.ExtensionForCodec(subtitle.Codec);
                var subtitlePath =
                    $"Videos/{embyItemId}/{subtitle.Path}/Subtitles/{subtitle.StreamIndex}/Stream.{extension}";

                DateTimeOffset exp = DateTimeOffset.Now + TimeSpan.FromHours(1);

                string sig = InternalUrlSigner.Sign(exp, "emby", subtitlePath);

                return $"http://localhost:{Settings.StreamingPort}/internal/media/emby/{subtitlePath}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
            }
        }

        return Option<string>.None;
    }
}
