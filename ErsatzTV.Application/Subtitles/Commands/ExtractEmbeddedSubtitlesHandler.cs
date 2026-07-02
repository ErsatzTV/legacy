using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Subtitles;

public class ExtractEmbeddedSubtitlesHandler : ExtractEmbeddedSubtitlesHandlerBase,
    IRequestHandler<ExtractEmbeddedSubtitles, Option<BaseError>>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IEntityLocker _entityLocker;
    private readonly ILogger<ExtractEmbeddedSubtitlesHandler> _logger;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public ExtractEmbeddedSubtitlesHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IFileSystem fileSystem,
        IEntityLocker entityLocker,
        IConfigElementRepository configElementRepository,
        ChannelWriter<IBackgroundServiceRequest> workerChannel,
        ILogger<ExtractEmbeddedSubtitlesHandler> logger)
        : base(fileSystem, logger)
    {
        _dbContextFactory = dbContextFactory;
        _entityLocker = entityLocker;
        _configElementRepository = configElementRepository;
        _workerChannel = workerChannel;
        _logger = logger;
    }

    public async Task<Option<BaseError>> Handle(
        ExtractEmbeddedSubtitles request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, string> validation = await FFmpegPathMustExist(dbContext, cancellationToken);
        return await validation.Match(
            async ffmpegPath =>
            {
                Option<BaseError> result = await ExtractAll(dbContext, request, ffmpegPath, cancellationToken);
                await _workerChannel.WriteAsync(new ReleaseMemory(false), cancellationToken);
                return result;
            },
            error => Task.FromResult<Option<BaseError>>(error.Join()));
    }

    private async Task<Option<BaseError>> ExtractAll(
        TvContext dbContext,
        ExtractEmbeddedSubtitles request,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        try
        {
            bool useEmbeddedSubtitles = await _configElementRepository
                .GetValue<bool>(ConfigElementKey.FFmpegUseEmbeddedSubtitles, cancellationToken)
                .IfNoneAsync(true);

            if (!useEmbeddedSubtitles)
            {
                _logger.LogDebug("Embedded subtitles are NOT enabled; nothing to extract");
                return Option<BaseError>.None;
            }

            bool extractEmbeddedSubtitles = await _configElementRepository
                .GetValue<bool>(ConfigElementKey.FFmpegExtractEmbeddedSubtitles, cancellationToken)
                .IfNoneAsync(false);

            if (!extractEmbeddedSubtitles)
            {
                _logger.LogDebug("Embedded subtitle extraction is NOT enabled");
                return Option<BaseError>.None;
            }

            DateTime now = DateTime.UtcNow;
            DateTime until = now.AddHours(1);

            var playoutIdsToCheck = new List<int>();

            // only check the requested playout if subtitles are enabled
            Option<Playout> requestedPlayout = await dbContext.Playouts
                .AsNoTracking()
                .Filter(p => p.Channel.SubtitleMode != ChannelSubtitleMode.None ||
                             p.ProgramSchedule.Items.Any(psi =>
                                 psi.SubtitleMode != null && psi.SubtitleMode != ChannelSubtitleMode.None)
                             || p.Channel.StreamSelectorMode == ChannelStreamSelectorMode.Custom)
                .SelectOneAsync(p => p.Id, p => p.Id == request.PlayoutId.IfNone(-1), cancellationToken);

            playoutIdsToCheck.AddRange(requestedPlayout.Map(p => p.Id));

            // check all playouts (that have subtitles enabled) if none were passed
            if (request.PlayoutId.IsNone)
            {
                playoutIdsToCheck = dbContext.Playouts
                    .AsNoTracking()
                    .Filter(p => p.Channel.SubtitleMode != ChannelSubtitleMode.None ||
                                 p.ProgramSchedule.Items.Any(psi =>
                                     psi.SubtitleMode != null && psi.SubtitleMode != ChannelSubtitleMode.None)
                                 || p.Channel.StreamSelectorMode == ChannelStreamSelectorMode.Custom)
                    .Map(p => p.Id)
                    .ToList();
            }

            if (playoutIdsToCheck.Count == 0)
            {
                foreach (int playoutId in request.PlayoutId)
                {
                    _logger.LogDebug(
                        "Playout {PlayoutId} does not have subtitles enabled; nothing to extract",
                        playoutId);
                    return Option<BaseError>.None;
                }

                _logger.LogDebug("No playouts have subtitles enabled; nothing to extract");
                return Option<BaseError>.None;
            }

            foreach (int playoutId in playoutIdsToCheck)
            {
                await _entityLocker.LockPlayout(playoutId);
            }

            _logger.LogDebug("Checking playouts {PlayoutIds} for text subtitles to extract", playoutIdsToCheck);

            // find all playout items in the next hour
            List<PlayoutItem> playoutItems = await dbContext.PlayoutItems
                .AsNoTracking()
                .Filter(pi => playoutIdsToCheck.Contains(pi.PlayoutId))
                .Filter(pi => pi.Finish >= DateTime.UtcNow)
                .Filter(pi => pi.Start <= until)
                .ToListAsync(cancellationToken);

            var mediaItemIds = playoutItems.Map(pi => pi.MediaItemId).ToList();

            // filter for items with text subtitles or font attachments
            List<int> mediaItemIdsWithTextSubtitles =
                await GetMediaItemIdsWithTextSubtitles(dbContext, mediaItemIds, cancellationToken);

            if (mediaItemIdsWithTextSubtitles.Count != 0)
            {
                _logger.LogDebug(
                    "Checking media items {MediaItemIds} for text subtitles or fonts to extract for playouts {PlayoutIds}",
                    mediaItemIdsWithTextSubtitles,
                    playoutIdsToCheck);
            }
            else
            {
                _logger.LogDebug(
                    "Found no text subtitles or fonts to extract for playouts {PlayoutIds}",
                    playoutIdsToCheck);
            }

            // sort by start time
            var toUpdate = playoutItems
                .Filter(pi => pi.Finish >= DateTime.UtcNow)
                .DistinctBy(pi => pi.MediaItemId)
                .Filter(pi => mediaItemIdsWithTextSubtitles.Contains(pi.MediaItemId))
                .OrderBy(pi => pi.StartOffset)
                .Map(pi => pi.MediaItemId)
                .ToList();

            foreach (int mediaItemId in toUpdate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Option<BaseError>.None;
                }

                // extract subtitles and fonts for each item and update db
                await ExtractSubtitles(dbContext, mediaItemId, ffmpegPath, cancellationToken);
                await ExtractFonts(dbContext, mediaItemId, ffmpegPath, cancellationToken);
            }

            _logger.LogDebug("Done checking playouts {PlayoutIds} for text subtitles to extract", playoutIdsToCheck);

            foreach (int playoutId in playoutIdsToCheck)
            {
                await _entityLocker.UnlockPlayout(playoutId);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // do nothing
        }

        return Option<BaseError>.None;
    }
}
