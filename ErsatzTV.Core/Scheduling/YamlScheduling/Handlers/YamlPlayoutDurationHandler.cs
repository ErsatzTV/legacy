using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;
using TimeSpanParserUtil;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutDurationHandler(EnumeratorCache enumeratorCache) : YamlPlayoutContentHandler(enumeratorCache)
{
    public override async Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutDurationInstruction duration)
        {
            return false;
        }

        // TODO: move to up-front validation somewhere
        if (!TimeSpanParser.TryParse(duration.Duration, out TimeSpan timeSpan))
        {
            return false;
        }

        if (!duration.StopBeforeEnd && duration.OfflineTail)
        {
            logger.LogError("offline_tail must be false when stop_before_end is false");
            return false;
        }

        DateTimeOffset targetTime = context.CurrentTime.Add(timeSpan);

        Option<IMediaCollectionEnumerator> maybeEnumerator = await GetContentEnumerator(
            context,
            duration.Content,
            logger,
            cancellationToken);

        Option<IMediaCollectionEnumerator> fallbackEnumerator = await GetContentEnumerator(
            context,
            duration.Fallback,
            logger,
            cancellationToken);

        foreach (IMediaCollectionEnumerator enumerator in maybeEnumerator)
        {
            context.CurrentTime = await Schedule(
                context,
                instruction.Content,
                duration.Fallback,
                targetTime,
                duration.StopBeforeEnd,
                duration.DiscardAttempts,
                duration.Trim,
                duration.OfflineTail,
                GetFillerKind(duration, context),
                duration.CustomTitle,
                duration.DisableWatermarks,
                enumerator,
                fallbackEnumerator,
                executeSequence,
                logger);

            return true;
        }

        return false;
    }

    protected static async Task<DateTimeOffset> Schedule(
        YamlPlayoutContext context,
        string contentKey,
        string fallbackContentKey,
        DateTimeOffset targetTime,
        bool stopBeforeEnd,
        int discardAttempts,
        bool trim,
        bool offlineTail,
        FillerKind fillerKind,
        string customTitle,
        bool disableWatermarks,
        IMediaCollectionEnumerator enumerator,
        Option<IMediaCollectionEnumerator> fallbackEnumerator,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger)
    {
        var done = false;
        TimeSpan remainingToFill = targetTime - context.CurrentTime;
        while (!done && enumerator.Current.IsSome && remainingToFill > TimeSpan.Zero)
        {
            foreach (string preRollSequence in context.GetPreRollSequence())
            {
                context.PushFillerKind(FillerKind.PreRoll);
                await executeSequence(preRollSequence);
                context.PopFillerKind();

                remainingToFill = targetTime - context.CurrentTime;
                if (remainingToFill <= TimeSpan.Zero)
                {
                    break;
                }
            }

            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = context.Playout.Id,
                    MediaItemId = mediaItem.Id,
                    Start = context.CurrentTime.UtcDateTime,
                    Finish = context.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    GuideGroup = context.PeekNextGuideGroup(),
                    FillerKind = fillerKind,
                    CustomTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle,
                    DisableWatermarks = disableWatermarks,
                    PlayoutItemWatermarks = [],
                    PlayoutItemGraphicsElements = []
                };

                foreach (int watermarkId in context.GetChannelWatermarkIds())
                {
                    playoutItem.PlayoutItemWatermarks.Add(
                        new PlayoutItemWatermark
                        {
                            PlayoutItem = playoutItem,
                            WatermarkId = watermarkId
                        });
                }

                foreach ((int graphicsElementId, string variablesJson) in context.GetGraphicsElements())
                {
                    playoutItem.PlayoutItemGraphicsElements.Add(
                        new PlayoutItemGraphicsElement
                        {
                            PlayoutItem = playoutItem,
                            GraphicsElementId = graphicsElementId,
                            Variables = variablesJson
                        });
                }

                if (remainingToFill - itemDuration >= TimeSpan.Zero || !stopBeforeEnd)
                {
                    context.AddedItems.Add(playoutItem);
                    context.AdvanceGuideGroup();

                    // create history record
                    List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                        context,
                        contentKey,
                        enumerator,
                        playoutItem,
                        mediaItem,
                        logger);

                    foreach (PlayoutHistory history in maybeHistory)
                    {
                        context.AddedHistory.Add(history);
                    }

                    remainingToFill -= itemDuration;
                    context.CurrentTime += itemDuration;

                    enumerator.MoveNext(playoutItem.StartOffset);
                }
                else if (discardAttempts > 0)
                {
                    // item won't fit; try the next one
                    discardAttempts--;
                    enumerator.MoveNext(Option<DateTimeOffset>.None);
                }
                else if (trim)
                {
                    // trim item to exactly fit
                    playoutItem.Finish = targetTime.UtcDateTime;
                    playoutItem.OutPoint = playoutItem.Finish - playoutItem.Start;

                    context.AddedItems.Add(playoutItem);
                    context.AdvanceGuideGroup();

                    // create history record
                    List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                        context,
                        contentKey,
                        enumerator,
                        playoutItem,
                        mediaItem,
                        logger);

                    foreach (PlayoutHistory history in maybeHistory)
                    {
                        context.AddedHistory.Add(history);
                    }

                    remainingToFill = TimeSpan.Zero;
                    context.CurrentTime = targetTime;

                    enumerator.MoveNext(playoutItem.StartOffset);
                }
                else if (fallbackEnumerator.IsSome)
                {
                    foreach (IMediaCollectionEnumerator fallback in fallbackEnumerator)
                    {
                        remainingToFill = TimeSpan.Zero;
                        context.CurrentTime = targetTime;
                        done = true;

                        // replace with fallback content
                        foreach (MediaItem fallbackItem in fallback.Current)
                        {
                            playoutItem.MediaItemId = fallbackItem.Id;
                            playoutItem.Finish = targetTime.UtcDateTime;

                            // this item was built for the content that did not fit, so its
                            // out point is that item's duration and has nothing to do with
                            // the fallback taking its place; leaving it makes the fallback
                            // claim to run longer than the gap it fills
                            playoutItem.OutPoint = playoutItem.Finish - playoutItem.Start;

                            playoutItem.FillerKind = FillerKind.Fallback;

                            context.AddedItems.Add(playoutItem);

                            // create history record
                            List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                                context,
                                fallbackContentKey,
                                fallback,
                                playoutItem,
                                mediaItem,
                                logger);

                            foreach (PlayoutHistory history in maybeHistory)
                            {
                                context.AddedHistory.Add(history);
                            }

                            fallback.MoveNext(playoutItem.StartOffset);
                        }
                    }
                }
                else
                {
                    // item won't fit; we're done
                    done = true;
                }
            }

            foreach (string postRollSequence in context.GetPostRollSequence())
            {
                context.PushFillerKind(FillerKind.PostRoll);
                await executeSequence(postRollSequence);
                context.PopFillerKind();
            }
        }

        if (!stopBeforeEnd)
        {
            return context.CurrentTime;
        }

        return offlineTail ? targetTime : context.CurrentTime;
    }
}
