using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling;

public class PlayoutModeSchedulerDuration(ILogger logger)
    : PlayoutModeSchedulerBase<ProgramScheduleItemDuration>(logger)
{
    public override PlayoutSchedulerResult Schedule(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItemDuration scheduleItem,
        ProgramScheduleItem nextScheduleItem,
        DateTimeOffset hardStop,
        Random random,
        CancellationToken cancellationToken)
    {
        var warnings = new PlayoutBuildWarnings();

        // Logger.LogDebug(
        //     "DurationSchedule: {ItemId} {CurrentTime} {DurationFinish} {InDurationFiller} {HardStop}",
        //     scheduleItem.Id,
        //     playoutBuilderState.CurrentTime,
        //     playoutBuilderState.DurationFinish,
        //     playoutBuilderState.InDurationFiller,
        //     hardStop);

        var playoutItems = new List<PlayoutItem>();

        PlayoutBuilderState nextState = playoutBuilderState;

        var willFinishInTime = true;
        Option<DateTimeOffset> durationUntil = None;
        var discardAttempts = 0;

        IMediaCollectionEnumerator contentEnumerator =
            collectionEnumerators[CollectionKey.ForScheduleItem(scheduleItem)];
        while (contentEnumerator.Current.IsSome && willFinishInTime &&
               nextState.CurrentTime < nextState.DurationFinish.IfNone(SystemTime.MaxValueUtc))
        {
            // Logger.LogDebug(
            //     "Duration Loop: CT: {CurrentTime} DF: {DurationFinish} Filler: {InDurationFiller} Stop: {HardStop}",
            //     nextState.CurrentTime,
            //     nextState.DurationFinish,
            //     nextState.InDurationFiller,
            //     hardStop);

            MediaItem mediaItem = contentEnumerator.Current.ValueUnsafe();

            // find when we should start this item, based on the current time
            DateTimeOffset itemStartTime = GetStartTimeAfter(nextState, scheduleItem, Option<ILogger>.Some(Logger));

            if (itemStartTime >= nextState.DurationFinish.IfNone(SystemTime.MaxValueUtc) ||
                // don't start if the first item will already be after the hard stop
                playoutItems.Count == 0 && itemStartTime >= hardStop)
            {
                nextState = nextState with { CurrentTime = hardStop };
                break;
            }

            // remember when we need to finish this duration item
            if (nextState.DurationFinish.IsNone)
            {
                nextState = nextState with
                {
                    DurationFinish = itemStartTime + scheduleItem.PlayoutDuration
                };

                // Logger.LogDebug("Setting duration finish to {DurationFinish}", nextState.DurationFinish);
            }

            durationUntil = nextState.DurationFinish;

            TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
            List<MediaChapter> itemChapters = ChaptersForMediaItem(mediaItem);

            if (itemDuration > scheduleItem.PlayoutDuration)
            {
                var fail = false;
                foreach (TimeSpan minimumDuration in contentEnumerator.MinimumDuration)
                {
                    if (minimumDuration > scheduleItem.PlayoutDuration)
                    {
                        Logger.LogError(
                            "Collection with minimum duration {Duration:hh\\:mm\\:ss} will never fit in schedule item with duration {PlayoutDuration:hh\\:mm\\:ss}; skipping this schedule item!",
                            minimumDuration,
                            scheduleItem.PlayoutDuration);

                        nextState = nextState with
                        {
                            DurationFinish = None
                        };

                        nextState.ScheduleItemsEnumerator.MoveNext();
                        fail = true;
                    }
                }

                if (fail)
                {
                    break;
                }

                Logger.LogWarning(
                    "Skipping playout item {Title} with duration {Duration:hh\\:mm\\:ss} that will never fit in schedule item duration {PlayoutDuration:hh\\:mm\\:ss}",
                    PlayoutBuilder.DisplayTitle(mediaItem),
                    itemDuration,
                    scheduleItem.PlayoutDuration);

                contentEnumerator.MoveNext(Option<DateTimeOffset>.None);
                continue;
            }

            TimeSpan remainingDuration = durationUntil.ValueUnsafe() - itemStartTime;
            if (scheduleItem.DiscardToFillAttempts > 0 &&
                remainingDuration >= contentEnumerator.MinimumDuration.IfNone(TimeSpan.Zero) &&
                itemDuration > remainingDuration)
            {
                discardAttempts++;
                if (discardAttempts > scheduleItem.DiscardToFillAttempts)
                {
                    nextState = nextState with
                    {
                        DurationFinish = None
                    };

                    nextState.ScheduleItemsEnumerator.MoveNext();
                    break;
                }

                Logger.LogDebug(
                    "Skipping playout item {Title} with duration {Duration:hh\\:mm\\:ss} that is longer than remaining duration {RemainingDuration:hh\\:mm\\:ss}",
                    PlayoutBuilder.DisplayTitle(mediaItem),
                    itemDuration,
                    remainingDuration);

                contentEnumerator.MoveNext(Option<DateTimeOffset>.None);
            }
            else
            {
                discardAttempts = 0;

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = playoutBuilderState.PlayoutId,
                    MediaItemId = mediaItem.Id,
                    Start = itemStartTime.UtcDateTime,
                    Finish = itemStartTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    GuideGroup = nextState.NextGuideGroup,
                    FillerKind = scheduleItem.GuideMode == GuideMode.Filler ||
                                 contentEnumerator.CurrentIncludeInProgramGuide == false
                        ? FillerKind.GuideMode
                        : FillerKind.None,
                    CustomTitle = scheduleItem.CustomTitle,
                    PreferredAudioLanguageCode = scheduleItem.PreferredAudioLanguageCode,
                    PreferredAudioTitle = scheduleItem.PreferredAudioTitle,
                    PreferredSubtitleLanguageCode = scheduleItem.PreferredSubtitleLanguageCode,
                    SubtitleMode = scheduleItem.SubtitleMode,
                    PlayoutItemWatermarks = [],
                    PlayoutItemGraphicsElements = [],
                    SchedulingContext = GetSchedulingContext(scheduleItem, null, contentEnumerator)
                };

                foreach (ProgramScheduleItemWatermark programScheduleItemWatermark in scheduleItem
                             .ProgramScheduleItemWatermarks ?? [])
                {
                    playoutItem.PlayoutItemWatermarks.Add(
                        new PlayoutItemWatermark
                        {
                            PlayoutItem = playoutItem,
                            WatermarkId = programScheduleItemWatermark.WatermarkId
                        });
                }

                foreach (ProgramScheduleItemGraphicsElement programScheduleItemGraphicsElement in scheduleItem
                             .ProgramScheduleItemGraphicsElements ?? [])
                {
                    playoutItem.PlayoutItemGraphicsElements.Add(
                        new PlayoutItemGraphicsElement
                        {
                            PlayoutItem = playoutItem,
                            GraphicsElementId = programScheduleItemGraphicsElement.GraphicsElementId
                        });
                }

                durationUntil.Do(du => playoutItem.GuideFinish = du.UtcDateTime);

                DateTimeOffset durationFinish = nextState.DurationFinish.IfNone(SystemTime.MaxValueUtc);

                var enumeratorStates = new Dictionary<CollectionKey, CollectionEnumeratorState>();
                foreach ((CollectionKey key, IMediaCollectionEnumerator enumerator) in collectionEnumerators)
                {
                    enumeratorStates.Add(key, enumerator.State.Clone());
                }

                List<PlayoutItem> maybePlayoutItems = AddFiller(
                    nextState,
                    collectionEnumerators,
                    scheduleItem,
                    playoutItem,
                    itemChapters,
                    warnings,
                    cancellationToken);

                // foreach (PlayoutItem pi in maybePlayoutItems.OrderBy(pi => pi.StartOffset))
                // {
                //     Logger.LogDebug(
                //         "  - PI Start: {Start} - {Id} - {FillerKind}",
                //         pi.StartOffset,
                //         pi.MediaItemId,
                //         pi.FillerKind);
                // }

                DateTimeOffset itemEndTimeWithFiller = maybePlayoutItems.Max(pi => pi.FinishOffset);

                willFinishInTime = itemStartTime > durationFinish ||
                                   itemEndTimeWithFiller <= durationFinish;
                if (willFinishInTime)
                {
                    // LogScheduledItem(scheduleItem, mediaItem, itemStartTime);
                    playoutItems.AddRange(maybePlayoutItems);

                    nextState = nextState with
                    {
                        CurrentTime = itemEndTimeWithFiller,

                        // only bump guide group if we don't have a custom title
                        NextGuideGroup = string.IsNullOrWhiteSpace(scheduleItem.CustomTitle)
                            ? nextState.IncrementGuideGroup
                            : nextState.NextGuideGroup
                    };

                    contentEnumerator.MoveNext(itemStartTime);
                }
                else
                {
                    // reset enumerators
                    foreach ((CollectionKey key, IMediaCollectionEnumerator enumerator) in collectionEnumerators)
                    {
                        enumerator.ResetState(enumeratorStates[key]);
                    }

                    TimeSpan durationBlock = itemEndTimeWithFiller - itemStartTime;
                    if (durationBlock > scheduleItem.PlayoutDuration)
                    {
                        Logger.LogWarning(
                            "Unable to schedule duration block of {DurationBlock:hh\\:mm\\:ss} which is longer than the configured playout duration {PlayoutDuration:hh\\:mm\\:ss}",
                            durationBlock,
                            scheduleItem.PlayoutDuration);
                    }

                    nextState = nextState with
                    {
                        DurationFinish = None
                    };

                    nextState.ScheduleItemsEnumerator.MoveNext();
                }
            }
        }

        // this is needed when the duration finish exactly matches the hard stop
        if (nextState.DurationFinish.IsSome && nextState.CurrentTime == nextState.DurationFinish)
        {
            nextState = nextState with
            {
                DurationFinish = None
            };

            nextState.ScheduleItemsEnumerator.MoveNext();
        }

        if (playoutItems.Select(pi => pi.GuideGroup).Distinct().Count() != 1)
        {
            nextState = nextState with { NextGuideGroup = nextState.DecrementGuideGroup };
        }

        foreach (DateTimeOffset nextItemStart in durationUntil)
        {
            switch (scheduleItem.TailMode)
            {
                case TailMode.Filler:
                    if (scheduleItem.TailFiller != null)
                    {
                        (nextState, playoutItems) = AddTailFiller(
                            nextState,
                            collectionEnumerators,
                            scheduleItem,
                            playoutItems,
                            nextItemStart,
                            warnings,
                            cancellationToken);
                    }

                    if (scheduleItem.FallbackFiller != null)
                    {
                        (nextState, playoutItems) = AddFallbackFiller(
                            nextState,
                            collectionEnumerators,
                            scheduleItem,
                            playoutItems,
                            nextItemStart,
                            cancellationToken);
                    }

                    nextState = nextState with { CurrentTime = nextItemStart };
                    break;
                case TailMode.Offline:
                    if (scheduleItem.FallbackFiller != null)
                    {
                        (nextState, playoutItems) = AddFallbackFiller(
                            nextState,
                            collectionEnumerators,
                            scheduleItem,
                            playoutItems,
                            nextItemStart,
                            cancellationToken);
                    }

                    nextState = nextState with { CurrentTime = nextItemStart };
                    break;
            }
        }

        bool hasFallback = playoutItems.Any(p => p.FillerKind == FillerKind.Fallback);

        var playoutItemsToClear = playoutItems
            .Filter(pi => pi.FillerKind == FillerKind.None)
            .ToList();

        PlayoutItem lastItem = playoutItemsToClear.MaxBy(pi => pi.FinishOffset);

        // if we've finished the duration or are in offline tail mode with no fallback, keep guide finish on the last item
        if (nextState.DurationFinish.IsNone && (scheduleItem.TailMode != TailMode.Offline || hasFallback))
        {
            playoutItemsToClear.Remove(lastItem);
        }

        foreach (PlayoutItem item in playoutItemsToClear)
        {
            item.GuideFinish = null;
        }

        nextState = nextState with { NextGuideGroup = nextState.IncrementGuideGroup };

        return new PlayoutSchedulerResult(nextState, playoutItems, warnings);
    }

    protected override string SchedulingContextName => "Duration";
}
