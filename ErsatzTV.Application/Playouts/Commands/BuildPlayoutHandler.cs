using System.Threading.Channels;
using EFCore.BulkExtensions;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Channel = ErsatzTV.Core.Domain.Channel;

namespace ErsatzTV.Application.Playouts;

public class BuildPlayoutHandler : IRequestHandler<BuildPlayout, Either<BaseError, Unit>>
{
    private readonly IBlockPlayoutBuilder _blockPlayoutBuilder;
    private readonly IBlockPlayoutFillerBuilder _blockPlayoutFillerBuilder;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IEntityLocker _entityLocker;
    private readonly IExternalJsonPlayoutBuilder _externalJsonPlayoutBuilder;
    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly IPlayoutBuilder _playoutBuilder;
    private readonly IPlayoutGapInserter _playoutGapInserter;
    private readonly IPlayoutTimeShifter _playoutTimeShifter;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;
    private readonly ILogger<BuildPlayoutHandler> _logger;
    private readonly ISequentialPlayoutBuilder _sequentialPlayoutBuilder;
    private readonly IScriptedPlayoutBuilder _scriptedPlayoutBuilder;

    public BuildPlayoutHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IPlayoutBuilder playoutBuilder,
        IBlockPlayoutBuilder blockPlayoutBuilder,
        IBlockPlayoutFillerBuilder blockPlayoutFillerBuilder,
        ISequentialPlayoutBuilder sequentialPlayoutBuilder,
        IScriptedPlayoutBuilder scriptedPlayoutBuilder,
        IExternalJsonPlayoutBuilder externalJsonPlayoutBuilder,
        IFFmpegSegmenterService ffmpegSegmenterService,
        IEntityLocker entityLocker,
        IPlayoutTimeShifter playoutTimeShifter,
        IPlayoutGapInserter playoutGapInserter,
        ChannelWriter<IBackgroundServiceRequest> workerChannel,
        ILogger<BuildPlayoutHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _playoutBuilder = playoutBuilder;
        _blockPlayoutBuilder = blockPlayoutBuilder;
        _blockPlayoutFillerBuilder = blockPlayoutFillerBuilder;
        _sequentialPlayoutBuilder = sequentialPlayoutBuilder;
        _scriptedPlayoutBuilder = scriptedPlayoutBuilder;
        _externalJsonPlayoutBuilder = externalJsonPlayoutBuilder;
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _entityLocker = entityLocker;
        _playoutTimeShifter = playoutTimeShifter;
        _playoutGapInserter = playoutGapInserter;
        _workerChannel = workerChannel;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> Handle(BuildPlayout request, CancellationToken cancellationToken)
    {
        try
        {
            await _entityLocker.LockPlayout(request.PlayoutId);
            if (request.Mode is not PlayoutBuildMode.Reset)
            {
                // this needs to happen before we load the playout in this handler because it modifies items, etc
                await _playoutTimeShifter.TimeShift(request.PlayoutId, DateTimeOffset.Now, false, cancellationToken);
            }

            Either<BaseError, PlayoutBuildResult> result;

            {
                await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                Validation<BaseError, Playout> validation = await Validate(dbContext, request, cancellationToken);
                result = await validation.Match(
                    playout => ApplyUpdateRequest(dbContext, request, playout, cancellationToken),
                    error => Task.FromResult<Either<BaseError, PlayoutBuildResult>>(error.Join()));
            }

            // after dbcontext is closed
            foreach (PlayoutBuildResult playoutBuildResult in result.RightToSeq())
            {
                foreach (DateTimeOffset timeShiftTo in playoutBuildResult.TimeShiftTo)
                {
                    await _playoutTimeShifter.TimeShift(request.PlayoutId, timeShiftTo, false, cancellationToken);
                }

                // must happen after any time shift, and before the playout is unlocked so that
                // the ui sees items and gaps from the same build
                await _playoutGapInserter.InsertGaps(request.PlayoutId, cancellationToken);

                if (playoutBuildResult.Warnings.TailFillerTooLong > 0)
                {
                    _logger.LogDebug(
                        "Playout {PlayoutId} skipped {Count} tail filler items that were too long to fit",
                        request.PlayoutId,
                        playoutBuildResult.Warnings.TailFillerTooLong);
                }

                if (playoutBuildResult.Warnings.MidRollContentWithoutChapters > 0)
                {
                    _logger.LogDebug(
                        "Playout {PlayoutId} converted mid-roll to post-roll for {Count} items that have no chapter markers",
                        request.PlayoutId,
                        playoutBuildResult.Warnings.MidRollContentWithoutChapters);
                }

                if (playoutBuildResult.Warnings.DurationFillerSkipped > 0)
                {
                    _logger.LogDebug(
                        "Playout {PlayoutId} skipped {Count} filler items to try to fit in a small remaining duration",
                        request.PlayoutId,
                        playoutBuildResult.Warnings.DurationFillerSkipped);
                }

                if (playoutBuildResult.Warnings.BlockItemSkippedEmptyCollection > 0)
                {
                    _logger.LogDebug(
                        "Playout {PlayoutId} skipped {Count} block items due to empty collections",
                        request.PlayoutId,
                        playoutBuildResult.Warnings.BlockItemSkippedEmptyCollection);
                }
            }

            return result.Map(_ => Unit.Default);
        }
        finally
        {
            await _entityLocker.UnlockPlayout(request.PlayoutId);
        }
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> ApplyUpdateRequest(
        TvContext dbContext,
        BuildPlayout request,
        Playout playout,
        CancellationToken cancellationToken)
    {
        var channelName = "[unknown]";

        await dbContext.PlayoutBuildStatus
            .Where(pbs => pbs.PlayoutId == playout.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var newBuildStatus = new PlayoutBuildStatus
        {
            PlayoutId = playout.Id,
            LastBuild = DateTimeOffset.Now
        };

        try
        {
            PlayoutReferenceData referenceData = await GetReferenceData(
                dbContext,
                playout.Id,
                playout.ScheduleKind);
            string channelNumber = referenceData.Channel.Number;
            channelName = referenceData.Channel.Name;
            Either<BaseError, PlayoutBuildResult> buildResult = BaseError.New("Unsupported schedule kind");

            switch (playout.ScheduleKind)
            {
                case PlayoutScheduleKind.Block:
                    buildResult = await _blockPlayoutBuilder.Build(
                        request.Start,
                        playout,
                        referenceData,
                        request.Mode,
                        cancellationToken);

                    foreach (var result in buildResult.RightToSeq())
                    {
                        buildResult = await _blockPlayoutFillerBuilder.Build(
                            playout,
                            referenceData,
                            result,
                            request.Mode,
                            cancellationToken);
                    }

                    break;
                case PlayoutScheduleKind.Sequential:
                    buildResult = await _sequentialPlayoutBuilder.Build(
                        request.Start,
                        playout,
                        referenceData,
                        request.Mode,
                        cancellationToken);
                    break;
                case PlayoutScheduleKind.Scripted:
                    buildResult = await _scriptedPlayoutBuilder.Build(
                        request.Start,
                        playout,
                        referenceData,
                        request.Mode,
                        cancellationToken);
                    break;
                case PlayoutScheduleKind.ExternalJson:
                    await _externalJsonPlayoutBuilder.Build(playout, request.Mode, cancellationToken);
                    break;
                case PlayoutScheduleKind.None:
                case PlayoutScheduleKind.Classic:
                default:
                    buildResult = await _playoutBuilder.Build(
                        request.Start,
                        playout,
                        referenceData,
                        request.Mode,
                        cancellationToken);
                    break;
            }

            return await buildResult.MatchAsync<Either<BaseError, PlayoutBuildResult>>(
                async result =>
                {
                    var changeCount = 0;

                    if (result.RerunHistoryToRemove.Count > 0)
                    {
                        changeCount += await dbContext.RerunHistory
                            .Where(rh => result.RerunHistoryToRemove.Contains(rh.Id))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    if (result.AddedRerunHistory.Count > 0)
                    {
                        changeCount += 1;
                        await dbContext.BulkInsertAsync(result.AddedRerunHistory, cancellationToken: cancellationToken);
                    }

                    if (result.ClearItems)
                    {
                        changeCount += await dbContext.PlayoutItems
                            .Where(pi => pi.PlayoutId == playout.Id)
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    foreach (DateTimeOffset removeBefore in result.RemoveBefore)
                    {
                        changeCount += await dbContext.PlayoutItems
                            .Where(pi => pi.PlayoutId == playout.Id)
                            .Where(pi => pi.Finish < removeBefore.UtcDateTime - referenceData.MaxPlayoutOffset)
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    foreach (DateTimeOffset removeAfter in result.RemoveAfter)
                    {
                        changeCount += await dbContext.PlayoutItems
                            .Where(pi => pi.PlayoutId == playout.Id)
                            .Where(pi => pi.Start >= removeAfter.UtcDateTime)
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    if (result.ItemsToRemove.Count > 0)
                    {
                        changeCount += await dbContext.PlayoutItems
                            .Where(pi => result.ItemsToRemove.Contains(pi.Id))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    if (result.AddedItems.Count > 0)
                    {
                        changeCount += 1;
                        bool anyWatermarks = result.AddedItems.Any(i =>
                            i.PlayoutItemWatermarks is not null && i.PlayoutItemWatermarks.Count > 0);
                        bool anyGraphicsElements = result.AddedItems.Any(i =>
                            i.PlayoutItemGraphicsElements is not null && i.PlayoutItemGraphicsElements.Count > 0);
                        if (anyWatermarks || anyGraphicsElements)
                        {
                            // need to use slow ef core to also insert watermarks and graphics elements properly
                            await dbContext.AddRangeAsync(result.AddedItems, cancellationToken);
                        }
                        else
                        {
                            // no watermarks or graphics, bulk insert is ok
                            await dbContext.BulkInsertAsync(result.AddedItems, cancellationToken: cancellationToken);
                        }
                    }

                    if (result.HistoryToRemove.Count > 0)
                    {
                        changeCount += await dbContext.PlayoutHistory
                            .Where(ph => result.HistoryToRemove.Contains(ph.Id))
                            .ExecuteDeleteAsync(cancellationToken);
                    }

                    if (result.AddedHistory.Count > 0)
                    {
                        changeCount += 1;
                        await dbContext.BulkInsertAsync(result.AddedHistory, cancellationToken: cancellationToken);
                    }

                    // let any active segmenter processes know that the playout has been modified
                    // and therefore the segmenter may need to seek into the next item instead of
                    // starting at the beginning (if already working ahead)
                    changeCount += await dbContext.SaveChangesAsync(cancellationToken);
                    bool hasChanges = changeCount > 0 || referenceData.Channel.PlayoutMode is ChannelPlayoutMode.OnDemand;

                    if (request.Mode != PlayoutBuildMode.Continue && hasChanges)
                    {
                        _ffmpegSegmenterService.PlayoutUpdated(referenceData.Channel.Number);
                    }

                    await _workerChannel.WriteAsync(
                        new CheckForOverlappingPlayoutItems(request.PlayoutId),
                        cancellationToken);

                    string fileName = Path.Combine(FileSystemLayout.ChannelGuideCacheFolder, $"{channelNumber}.xml");
                    if (hasChanges || !File.Exists(fileName) ||
                        playout.ScheduleKind is PlayoutScheduleKind.ExternalJson)
                    {
                        await _workerChannel.WriteAsync(new RefreshChannelData(channelNumber), cancellationToken);

                        // refresh guide data for all mirror channels, too
                        List<string> maybeMirrors = await dbContext.Channels
                            .AsNoTracking()
                            .Filter(c => c.MirrorSourceChannelId == referenceData.Channel.Id)
                            .Map(c => c.Number)
                            .ToListAsync(cancellationToken);
                        foreach (string mirror in maybeMirrors)
                        {
                            await _workerChannel.WriteAsync(new RefreshChannelData(mirror), cancellationToken);
                        }
                    }

                    await _workerChannel.WriteAsync(new SyncNextPlayout(channelNumber), cancellationToken);

                    await _workerChannel.WriteAsync(new ExtractEmbeddedSubtitles(playout.Id), cancellationToken);

                    newBuildStatus.Success = true;

                    return result;
                },
                error =>
                {
                    newBuildStatus.Success = false;
                    newBuildStatus.Message = error.Value;

                    return error;
                });
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            newBuildStatus.Success = false;
            newBuildStatus.Message = $"Timeout building playout for channel {channelName}";

            return BaseError.New(
                $"Timeout building playout for channel {channelName}; this may be a bug!");
        }
        catch (Exception ex)
        {
            DebugBreak.Break();

            newBuildStatus.Success = false;
            newBuildStatus.Message = $"Unexpected error building playout for channel {channelName}: {ex}";

            return BaseError.New(
                $"Unexpected error building playout for channel {channelName}: {ex.Message}");
        }
        finally
        {
            try
            {
                await dbContext.PlayoutBuildStatus.AddAsync(newBuildStatus, CancellationToken.None);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // do nothing
            }
        }
    }

    private static Task<Validation<BaseError, Playout>> Validate(
        TvContext dbContext,
        BuildPlayout request,
        CancellationToken cancellationToken) =>
        PlayoutMustExist(dbContext, request, cancellationToken).BindT(DiscardAttemptsMustBeValid);

    private static Validation<BaseError, Playout> DiscardAttemptsMustBeValid(Playout playout)
    {
        foreach (ProgramScheduleItemDuration item in
                 playout.ProgramSchedule?.Items.OfType<ProgramScheduleItemDuration>() ?? [])
        {
            item.DiscardToFillAttempts = item.PlaybackOrder switch
            {
                PlaybackOrder.Random or PlaybackOrder.Shuffle => item.DiscardToFillAttempts,
                _ => 0
            };
        }

        return playout;
    }

    private static async Task<Validation<BaseError, Playout>> PlayoutMustExist(
        TvContext dbContext,
        BuildPlayout buildPlayout,
        CancellationToken cancellationToken)
    {
        Option<Playout> maybePlayout = await dbContext.Playouts
            .Include(p => p.Anchor)
            .SelectOneAsync(p => p.Id, p => p.Id == buildPlayout.PlayoutId, cancellationToken);

        foreach (Playout playout in maybePlayout)
        {
            switch (playout.ScheduleKind)
            {
                case PlayoutScheduleKind.Classic:
                    await dbContext.Entry(playout)
                        .Collection(p => p.FillGroupIndices)
                        .LoadAsync(cancellationToken);

                    foreach (PlayoutScheduleItemFillGroupIndex fillGroupIndex in playout.FillGroupIndices)
                    {
                        await dbContext.Entry(fillGroupIndex)
                            .Reference(fgi => fgi.EnumeratorState)
                            .LoadAsync(cancellationToken);
                    }

                    await dbContext.Entry(playout)
                        .Collection(p => p.ProgramScheduleAnchors)
                        .LoadAsync(cancellationToken);

                    foreach (PlayoutProgramScheduleAnchor anchor in playout.ProgramScheduleAnchors)
                    {
                        await dbContext.Entry(anchor)
                            .Reference(a => a.EnumeratorState)
                            .LoadAsync(cancellationToken);
                    }

                    break;
            }
        }

        return maybePlayout.ToValidation<BaseError>("Playout does not exist.");
    }

    private static async Task<PlayoutReferenceData> GetReferenceData(
        TvContext dbContext,
        int playoutId,
        PlayoutScheduleKind scheduleKind)
    {
        Channel channel = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.Playouts.Any(p => p.Id == playoutId))
            .FirstOrDefaultAsync();

        TimeSpan maxPlayoutOffset = TimeSpan.Zero;
        List<Channel> mirrorChannels = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.MirrorSourceChannelId == channel.Id)
            .ToListAsync();
        foreach (var mirrorChannel in mirrorChannels)
        {
            var offset = mirrorChannel.PlayoutOffset ?? TimeSpan.Zero;
            if (offset > maxPlayoutOffset)
            {
                maxPlayoutOffset = offset;
            }
        }

        Option<Deco> deco = Option<Deco>.None;
        List<PlayoutItem> existingItems = [];
        List<PlayoutTemplate> playoutTemplates = [];

        if (scheduleKind is PlayoutScheduleKind.Block)
        {
            deco = await dbContext.Decos
                .AsNoTracking()
                .Include(d => d.BreakContent)
                .Where(d => d.Playouts.Any(p => p.Id == playoutId))
                .FirstOrDefaultAsync()
                .Map(Optional);

            existingItems = await dbContext.PlayoutItems
                .AsNoTracking()
                .Where(pi => pi.PlayoutId == playoutId)
                .ToListAsync();

            playoutTemplates = await dbContext.PlayoutTemplates
                .AsNoTracking()
                .Where(pt => pt.PlayoutId == playoutId)
                .Include(t => t.Template)
                .ThenInclude(t => t.Items)
                .ThenInclude(i => i.Block)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.BlockItemWatermarks)
                .Include(t => t.Template)
                .ThenInclude(t => t.Items)
                .ThenInclude(i => i.Block)
                .ThenInclude(b => b.Items)
                .ThenInclude(i => i.BlockItemGraphicsElements)
                .Include(t => t.DecoTemplate)
                .ThenInclude(t => t.Items)
                .ThenInclude(i => i.Deco)
                .ThenInclude(d => d.BreakContent)
                .ToListAsync();
        }

        ProgramSchedule programSchedule = await dbContext.ProgramSchedules
            .AsNoTracking()
            .Where(ps => ps.Playouts.Any(p => p.Id == playoutId))
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.ProgramScheduleItemWatermarks)
            .ThenInclude(psi => psi.Watermark)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.ProgramScheduleItemGraphicsElements)
            .ThenInclude(psi => psi.GraphicsElement)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.Collection)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.MediaItem)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.PreRollFiller)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.MidRollFiller)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.PostRollFiller)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.TailFiller)
            .Include(ps => ps.Items)
            .ThenInclude(psi => psi.FallbackFiller)
            .FirstOrDefaultAsync();

        List<ProgramScheduleAlternate> programScheduleAlternates = await dbContext.ProgramScheduleAlternates
            .AsNoTracking()
            .Where(pt => pt.PlayoutId == playoutId)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.ProgramScheduleItemWatermarks)
            .ThenInclude(psi => psi.Watermark)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.ProgramScheduleItemGraphicsElements)
            .ThenInclude(psi => psi.GraphicsElement)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.Collection)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.MediaItem)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.PreRollFiller)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.MidRollFiller)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.PostRollFiller)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.TailFiller)
            .Include(a => a.ProgramSchedule)
            .ThenInclude(ps => ps.Items)
            .ThenInclude(psi => psi.FallbackFiller)
            .ToListAsync();

        List<PlayoutHistory> playoutHistory = await dbContext.PlayoutHistory
            .AsNoTracking()
            .Where(h => h.PlayoutId == playoutId)
            .ToListAsync();

        return new PlayoutReferenceData(
            channel,
            deco,
            existingItems,
            playoutTemplates,
            programSchedule,
            programScheduleAlternates,
            playoutHistory,
            maxPlayoutOffset);
    }
}
