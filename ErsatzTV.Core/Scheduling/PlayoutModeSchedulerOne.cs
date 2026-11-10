using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling;

public class PlayoutModeSchedulerOne(ILogger logger) : PlayoutModeSchedulerBase<ProgramScheduleItemOne>(logger)
{
    public override PlayoutSchedulerResult Schedule(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItemOne scheduleItem,
        ProgramScheduleItem nextScheduleItem,
        DateTimeOffset hardStop,
        Random random,
        CancellationToken cancellationToken)
    {
        var warnings = new PlayoutBuildWarnings();

        IMediaCollectionEnumerator contentEnumerator =
            collectionEnumerators[CollectionKey.ForScheduleItem(scheduleItem)];
        foreach (MediaItem mediaItem in contentEnumerator.Current)
        {
            // find when we should start this item, based on the current time
            DateTimeOffset itemStartTime = GetStartTimeAfter(
                playoutBuilderState,
                scheduleItem,
                Option<ILogger>.Some(Logger));
            if (itemStartTime >= hardStop)
            {
                playoutBuilderState = playoutBuilderState with { CurrentTime = hardStop };
                break;
            }

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
                GuideGroup = playoutBuilderState.NextGuideGroup,
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

            List<PlayoutItem> playoutItems = AddFiller(
                playoutBuilderState,
                collectionEnumerators,
                scheduleItem,
                playoutItem,
                itemChapters,
                warnings,
                random,
                cancellationToken);

            PlayoutBuilderState nextState = playoutBuilderState with
            {
                CurrentTime = playoutItems.Max(pi => pi.FinishOffset)
            };

            nextState.ScheduleItemsEnumerator.MoveNext();
            contentEnumerator.MoveNext(itemStartTime);

            // LogScheduledItem(scheduleItem, mediaItem, itemStartTime);

            // only play one item from collection, so always advance to the next item
            // Logger.LogDebug(
            //     "Advancing to next schedule item after playout mode {PlayoutMode}",
            //     "One");

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

        return new PlayoutSchedulerResult(playoutBuilderState, [], warnings);
    }

    protected override string SchedulingContextName => "One";
}
