using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling;

public class PlayoutModeSchedulerMultiple(Map<CollectionKey, int> collectionItemCount, ILogger logger)
    : PlayoutModeSchedulerBase<ProgramScheduleItemMultiple>(logger)
{
    public override PlayoutSchedulerResult Schedule(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItemMultiple scheduleItem,
        ProgramScheduleItem nextScheduleItem,
        DateTimeOffset hardStop,
        Random random,
        CancellationToken cancellationToken)
    {
        var warnings = new PlayoutBuildWarnings();
        var playoutItems = new List<PlayoutItem>();

        DateTimeOffset firstStart = GetStartTimeAfter(playoutBuilderState, scheduleItem, Option<ILogger>.Some(Logger));
        if (firstStart >= hardStop)
        {
            playoutBuilderState = playoutBuilderState with { CurrentTime = hardStop };
            return new PlayoutSchedulerResult(playoutBuilderState, playoutItems, warnings);
        }

        IMediaCollectionEnumerator contentEnumerator =
            collectionEnumerators[CollectionKey.ForScheduleItem(scheduleItem)];

        PlayoutBuilderState nextState = playoutBuilderState with
        {
            CurrentTime = firstStart,
            MultipleRemaining = playoutBuilderState.MultipleRemaining.IfNone(
                CountExpression.Evaluate(scheduleItem.Count, contentEnumerator, random, cancellationToken))
        };

        if (nextState.MultipleRemaining == 0)
        {
            switch (scheduleItem.MultipleMode)
            {
                case MultipleMode.CollectionSize:
                    nextState = nextState with
                    {
                        MultipleRemaining = collectionItemCount[CollectionKey.ForScheduleItem(scheduleItem)]
                    };
                    break;
                case MultipleMode.PlaylistItemSize:
                    if (contentEnumerator is PlaylistEnumerator { CurrentEnumeratorPlayAll: true } playlistEnumerator)
                    {
                        nextState = nextState with
                        {
                            MultipleRemaining = playlistEnumerator
                                .ChildEnumerators[playlistEnumerator.EnumeratorIndex]
                                .Enumerator.Count
                        };
                    }

                    break;
                case MultipleMode.MultiEpisodeGroupSize:
                    if (contentEnumerator is ChronologicalMediaCollectionEnumerator chronologicalEnumerator)
                    {
                        foreach (MediaItem current in contentEnumerator.Current)
                        {
                            nextState = nextState with
                            {
                                MultipleRemaining = chronologicalEnumerator.GroupSizeForMediaItem(current)
                            };
                        }
                    }

                    break;
            }
        }

        while (contentEnumerator.Current.IsSome && nextState.MultipleRemaining > 0 &&
               nextState.CurrentTime < hardStop)
        {
            MediaItem mediaItem = contentEnumerator.Current.ValueUnsafe();

            // find when we should start this item, based on the current time
            DateTimeOffset itemStartTime = GetStartTimeAfter(nextState, scheduleItem, Option<ILogger>.Some(Logger));

            TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
            List<MediaChapter> itemChapters = ChaptersForMediaItem(mediaItem);

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

            // LogScheduledItem(scheduleItem, mediaItem, itemStartTime);

            playoutItems.AddRange(
                AddFiller(
                    nextState,
                    collectionEnumerators,
                    scheduleItem,
                    playoutItem,
                    itemChapters,
                    warnings,
                    random,
                    cancellationToken));

            nextState = nextState with
            {
                CurrentTime = playoutItems.Max(pi => pi.FinishOffset),
                MultipleRemaining = nextState.MultipleRemaining.Map(i => i - 1),

                // only bump guide group if we don't have a custom title
                NextGuideGroup = string.IsNullOrWhiteSpace(scheduleItem.CustomTitle)
                    ? nextState.IncrementGuideGroup
                    : nextState.NextGuideGroup
            };

            contentEnumerator.MoveNext(playoutItem.StartOffset);
        }

        if (nextState.MultipleRemaining.IfNone(-1) == 0)
        {
            // _logger.LogDebug(
            //     "Advancing to next schedule item after playout mode {PlayoutMode}",
            //     "Multiple");

            nextState = nextState with
            {
                MultipleRemaining = None,

                // only decrement guide group if it was bumped
                NextGuideGroup = playoutItems.Select(pi => pi.GuideGroup).Distinct().Count() != 1
                    ? nextState.DecrementGuideGroup
                    : nextState.NextGuideGroup
            };

            nextState.ScheduleItemsEnumerator.MoveNext();
        }

        DateTimeOffset nextItemStart = GetFillerStartTimeAfter(nextState, nextScheduleItem, hardStop);

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

        nextState = nextState with { NextGuideGroup = nextState.IncrementGuideGroup };

        return new PlayoutSchedulerResult(nextState, playoutItems, warnings);
    }

    protected override string SchedulingContextName => "Multiple";
}
