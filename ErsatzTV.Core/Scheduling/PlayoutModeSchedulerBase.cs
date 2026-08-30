using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using Humanizer;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling;

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types")]
public abstract class PlayoutModeSchedulerBase<T>(ILogger logger) : IPlayoutModeScheduler<T>
    where T : ProgramScheduleItem
{
    // half a day, so that a flexible item always moves to whichever occurrence of its start time
    // is nearest; waiting longer than this would mean the start time is one we already missed
    // ReSharper disable once StaticMemberInGenericType
    private static readonly TimeSpan MaximumFlexibleWait = TimeSpan.FromHours(12);

    // ReSharper disable once StaticMemberInGenericType
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly Random _random = new();

    protected ILogger Logger { get; } = logger;

    public abstract PlayoutSchedulerResult Schedule(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        T scheduleItem,
        ProgramScheduleItem nextScheduleItem,
        DateTimeOffset hardStop,
        Random random,
        CancellationToken cancellationToken);

    public static DateTimeOffset GetFillerStartTimeAfter(
        PlayoutBuilderState state,
        ProgramScheduleItem scheduleItem,
        DateTimeOffset hardStop)
    {
        DateTimeOffset startTime = GetStartTimeAfter(state, scheduleItem, Option<ILogger>.None);

        // filler should always stop at the hard stop
        if (hardStop < startTime)
        {
            startTime = hardStop;
        }

        return startTime;
    }

    public static DateTimeOffset GetStartTimeAfter(
        PlayoutBuilderState state,
        ProgramScheduleItem scheduleItem,
        Option<ILogger> maybeLogger,
        bool isPeek = false)
    {
        DateTimeOffset startTime = state.CurrentTime.ToLocalTime();

        bool isIncomplete = !isPeek &&
                            (scheduleItem is ProgramScheduleItemMultiple && state.MultipleRemaining.IsSome ||
                             scheduleItem is ProgramScheduleItemDuration && state.DurationFinish.IsSome ||
                             scheduleItem is ProgramScheduleItemFlood && state.InFlood ||
                             scheduleItem is ProgramScheduleItemDuration && state.InDurationFiller);

        if (scheduleItem.StartType == StartType.Fixed && !isIncomplete)
        {
            TimeSpan itemStartTime = scheduleItem.StartTime.GetValueOrDefault();

            DateTime date = startTime.Date;
            var withStartTime = new DateTime(
                date.Year,
                date.Month,
                date.Day,
                0,
                0,
                0,
                DateTimeKind.Unspecified).Add(itemStartTime);

            DateTimeOffset result = new DateTimeOffset(withStartTime, TimeZoneInfo.Local.GetUtcOffset(withStartTime));

            // tomorrow's occurrence of the same start time; the offset has to be re-derived rather than
            // carried over from result, or a DST change between the two shifts the wall clock by the delta
            DateTime tomorrowWithStartTime = withStartTime.AddDays(1);
            DateTimeOffset tomorrowResult = new DateTimeOffset(
                tomorrowWithStartTime,
                TimeZoneInfo.Local.GetUtcOffset(tomorrowWithStartTime));

            // Serilog.Log.Logger.Debug(
            //     "StartTimeOfDay: {StartTimeOfDay} Item Start Time: {ItemStartTime}",
            //     startTime.TimeOfDay.TotalMilliseconds,
            //     itemStartTime.TotalMilliseconds);

            // need to wrap to the next day if appropriate
            FixedStartTimeBehavior? fixedStartTimeBehavior = scheduleItem.FixedStartTimeBehavior;
            if (fixedStartTimeBehavior is null && scheduleItem.ProgramSchedule is not null)
            {
                fixedStartTimeBehavior = scheduleItem.ProgramSchedule.FixedStartTimeBehavior;
            }

            switch (fixedStartTimeBehavior)
            {
                case FixedStartTimeBehavior.Flexible:
                    // if we are peeking from a flood and the flexible time is in the past,
                    // we should use the next day's time to allow the flood to continue.
                    if (isPeek && state.InFlood && startTime > result)
                    {
                        startTime = tomorrowResult;
                    }
                    else
                    {
                        // wait for the next occurrence of the start time, unless it is far enough away
                        // that the occurrence we just missed is the nearer one - waiting for that would
                        // add most of a day of unscheduled time, which is what flexible exists to avoid
                        DateTimeOffset nextOccurrence = result >= startTime ? result : tomorrowResult;
                        if (nextOccurrence - startTime <= MaximumFlexibleWait)
                        {
                            startTime = nextOccurrence;
                        }
                    }

                    break;
                case FixedStartTimeBehavior.Strict:
                default:
                    startTime = startTime.TimeOfDay > itemStartTime ? result.AddDays(1) : result;
                    break;
            }

            TimeSpan gap = startTime - state.CurrentTime;
            if (gap > TimeSpan.FromHours(1) && fixedStartTimeBehavior == FixedStartTimeBehavior.Strict &&
                result.TimeOfDay < state.CurrentTime.ToLocalTime().TimeOfDay)
            {
                foreach (ILogger logger in maybeLogger)
                {
                    logger.LogWarning(
                        "Offline playout gap of {Gap} caused by strict fixed start time {StartTime} before current time {CurrentTime} on schedule {Name}",
                        gap.Humanize(),
                        result.TimeOfDay,
                        state.CurrentTime.TimeOfDay,
                        scheduleItem.ProgramSchedule?.Name ?? "unknown");
                }
            }
        }

        return startTime;
    }

    protected Tuple<PlayoutBuilderState, List<PlayoutItem>> AddTailFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItem scheduleItem,
        List<PlayoutItem> playoutItems,
        DateTimeOffset nextItemStart,
        PlayoutBuildWarnings warnings,
        CancellationToken cancellationToken)
    {
        var newItems = new List<PlayoutItem>(playoutItems);
        PlayoutBuilderState nextState = playoutBuilderState;

        if (scheduleItem.TailFiller != null)
        {
            IMediaCollectionEnumerator enumerator =
                collectionEnumerators[CollectionKey.ForFillerPreset(scheduleItem.TailFiller)];

            while (enumerator.Current.IsSome && nextState.CurrentTime < nextItemStart)
            {
                MediaItem mediaItem = enumerator.Current.ValueUnsafe();

                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
                TimeSpan inPoint = InPointForMediaItem(mediaItem);

                if (nextState.CurrentTime + itemDuration > nextItemStart)
                {
                    warnings.TailFillerTooLong++;
                    break;
                }

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = playoutBuilderState.PlayoutId,
                    MediaItemId = IdForMediaItem(mediaItem),
                    Start = nextState.CurrentTime.UtcDateTime,
                    Finish = nextState.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = inPoint,
                    OutPoint = inPoint + itemDuration,
                    FillerKind = FillerKind.Tail,
                    GuideGroup = nextState.NextGuideGroup,
                    DisableWatermarks = !scheduleItem.TailFiller.AllowWatermarks,
                    ChapterTitle = ChapterTitleForMediaItem(mediaItem),
                    SchedulingContext = GetSchedulingContext(scheduleItem, scheduleItem.TailFillerId, enumerator)
                };

                newItems.Add(playoutItem);

                nextState = nextState with
                {
                    CurrentTime = nextState.CurrentTime + itemDuration
                };

                enumerator.MoveNext(playoutItem.StartOffset);
            }
        }

        return Tuple(nextState, newItems);
    }

    protected Tuple<PlayoutBuilderState, List<PlayoutItem>> AddFallbackFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItem scheduleItem,
        List<PlayoutItem> playoutItems,
        DateTimeOffset nextItemStart,
        CancellationToken cancellationToken)
    {
        var newItems = new List<PlayoutItem>(playoutItems);
        PlayoutBuilderState nextState = playoutBuilderState;

        if (scheduleItem.FallbackFiller != null && playoutBuilderState.CurrentTime < nextItemStart)
        {
            IMediaCollectionEnumerator enumerator =
                collectionEnumerators[CollectionKey.ForFillerPreset(scheduleItem.FallbackFiller)];

            while (enumerator.Current.IsSome && nextState.CurrentTime < nextItemStart)
            {
                MediaItem mediaItem = enumerator.Current.ValueUnsafe();

                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
                TimeSpan gap = nextItemStart - nextState.CurrentTime;
                TimeSpan duration = itemDuration < gap ? itemDuration : gap;
                TimeSpan inPoint = InPointForMediaItem(mediaItem);

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = playoutBuilderState.PlayoutId,
                    MediaItemId = IdForMediaItem(mediaItem),
                    Start = nextState.CurrentTime.UtcDateTime,
                    Finish = nextState.CurrentTime.Add(duration).UtcDateTime,
                    InPoint = inPoint,
                    OutPoint = inPoint + duration,
                    GuideGroup = nextState.NextGuideGroup,
                    FillerKind = FillerKind.Fallback,
                    DisableWatermarks = !scheduleItem.FallbackFiller.AllowWatermarks,
                    ChapterTitle = ChapterTitleForMediaItem(mediaItem),
                    SchedulingContext = GetSchedulingContext(scheduleItem, scheduleItem.FallbackFillerId, enumerator)
                };

                newItems.Add(playoutItem);

                nextState = nextState with
                {
                    CurrentTime = nextState.CurrentTime.Add(duration)
                };

                enumerator.MoveNext(playoutItem.StartOffset);
            }
        }

        return Tuple(nextState, newItems);
    }

    private static TimeSpan InPointForMediaItem(MediaItem mediaItem) =>
        mediaItem switch
        {
            ChapterMediaItem c => c.MediaVersion.InPoint,
            _ => TimeSpan.Zero
        };

    private static int IdForMediaItem(MediaItem mediaItem) =>
        mediaItem switch
        {
            ChapterMediaItem c => c.MediaItemId,
            _ => mediaItem.Id
        };

    private static string ChapterTitleForMediaItem(MediaItem mediaItem) =>
        mediaItem switch
        {
            ChapterMediaItem c => c.MediaVersion.Title,
            _ => null
        };

    protected static List<MediaChapter> ChaptersForMediaItem(MediaItem mediaItem)
    {
        MediaVersion version = mediaItem.GetHeadVersion();
        return Optional(version.Chapters).Flatten().OrderBy(c => c.StartTime).ToList();
    }

    protected void LogScheduledItem(
        ProgramScheduleItem scheduleItem,
        MediaItem mediaItem,
        DateTimeOffset startTime) =>
        Logger.LogDebug(
            "Scheduling media item: {ScheduleItemNumber} / {CollectionType} / {MediaItemId} - {MediaItemTitle} / {StartTime}",
            scheduleItem.Index,
            scheduleItem.CollectionType,
            mediaItem.Id,
            PlayoutBuilder.DisplayTitle(mediaItem),
            startTime);

    internal List<PlayoutItem> AddFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> enumerators,
        ProgramScheduleItem scheduleItem,
        PlayoutItem playoutItem,
        List<MediaChapter> chapters,
        PlayoutBuildWarnings warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        var allFiller = Optional(scheduleItem.PreRollFiller)
            .Append(Optional(scheduleItem.MidRollFiller))
            .Append(Optional(scheduleItem.PostRollFiller))
            .ToList();

        // multiple pad-to-nearest-minute values are invalid; use no filler
        if (allFiller.Count(f => f.FillerMode == FillerMode.Pad && f.PadToNearestMinute.HasValue) > 1)
        {
            Logger.LogError("Multiple pad-to-nearest-minute values are invalid; no filler will be used");
            return [playoutItem];
        }

        // missing pad-to-nearest-minute value is invalid; use no filler
        FillerPreset invalidPadFiller = allFiller
            .FirstOrDefault(f => f.FillerMode == FillerMode.Pad && !f.PadToNearestMinute.HasValue);
        if (invalidPadFiller is not null)
        {
            Logger.LogError(
                "Pad filler ({Filler}) without pad-to-nearest-minute value is invalid; no filler will be used",
                invalidPadFiller.Name);
            return [playoutItem];
        }

        List<MediaChapter> effectiveChapters = chapters;
        if (allFiller.All(fp => fp.FillerKind != FillerKind.MidRoll) || effectiveChapters.Count <= 1)
        {
            effectiveChapters = [];
        }

        // convert mid-roll to post-roll if we have no chapters
        if (allFiller.Any(f => f.FillerKind is FillerKind.MidRoll) && effectiveChapters.Count == 0)
        {
            warnings.MidRollContentWithoutChapters++;

            var toRemove = allFiller.Filter(f => f.FillerKind is FillerKind.MidRoll).ToList();
            allFiller.RemoveAll(toRemove.Contains);

            foreach (FillerPreset midRollFiller in toRemove)
            {
                var clone = new FillerPreset
                {
                    FillerKind = FillerKind.PostRoll,
                    FillerMode = midRollFiller.FillerMode,
                    Duration = midRollFiller.Duration,
                    Count = midRollFiller.Count,
                    PadToNearestMinute = midRollFiller.PadToNearestMinute,
                    AllowWatermarks = midRollFiller.AllowWatermarks,
                    CollectionType = midRollFiller.CollectionType,
                    CollectionId = midRollFiller.CollectionId,
                    Collection = midRollFiller.Collection,
                    MediaItemId = midRollFiller.MediaItemId,
                    MediaItem = midRollFiller.MediaItem,
                    MultiCollectionId = midRollFiller.MultiCollectionId,
                    MultiCollection = midRollFiller.MultiCollection,
                    SmartCollectionId = midRollFiller.SmartCollectionId,
                    SmartCollection = midRollFiller.SmartCollection,
                    PlaylistId = midRollFiller.PlaylistId,
                    Playlist = midRollFiller.Playlist
                };

                allFiller.Add(clone);
            }
        }

        // convert playlist filler
        if (allFiller.Any(f => f.CollectionType is CollectionType.Playlist))
        {
            var toRemove = allFiller.Filter(f => f.CollectionType is CollectionType.Playlist)
                .ToList();
            allFiller.RemoveAll(toRemove.Contains);

            foreach (FillerPreset playlistFiller in toRemove)
            {
                var clone = new FillerPreset
                {
                    FillerKind = playlistFiller.FillerKind,
                    FillerMode = playlistFiller.FillerMode,
                    Duration = playlistFiller.Duration,
                    Count = playlistFiller.Count,
                    PadToNearestMinute = playlistFiller.PadToNearestMinute,
                    AllowWatermarks = playlistFiller.AllowWatermarks,
                    CollectionType = playlistFiller.CollectionType,
                    CollectionId = playlistFiller.CollectionId,
                    Collection = playlistFiller.Collection,
                    MediaItemId = playlistFiller.MediaItemId,
                    MediaItem = playlistFiller.MediaItem,
                    MultiCollectionId = playlistFiller.MultiCollectionId,
                    MultiCollection = playlistFiller.MultiCollection,
                    SmartCollectionId = playlistFiller.SmartCollectionId,
                    SmartCollection = playlistFiller.SmartCollection,
                    PlaylistId = playlistFiller.PlaylistId,
                    Playlist = playlistFiller.Playlist,
                    Expression = playlistFiller.Expression,
                    UseChaptersAsMediaItems = playlistFiller.UseChaptersAsMediaItems
                };

                // if filler count is 2, we need to schedule 2 * (number of items in one full playlist iteration)
                IMediaCollectionEnumerator fillerEnumerator =
                    enumerators[CollectionKey.ForFillerPreset(playlistFiller)];
                if (fillerEnumerator is PlaylistEnumerator playlistEnumerator)
                {
                    clone.Count *= playlistEnumerator.CountForFiller;
                }

                allFiller.Add(clone);
            }
        }

        foreach (FillerPreset filler in allFiller.Filter(f =>
                     f.FillerKind == FillerKind.PreRoll && f.FillerMode != FillerMode.Pad))
        {
            switch (filler.FillerMode)
            {
                case FillerMode.Duration when filler.Duration.HasValue:
                    IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            e1,
                            filler.Duration.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PreRoll,
                            filler.AllowWatermarks,
                            warnings));
                    break;
                case FillerMode.Count when filler.Count.HasValue:
                    IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddCountFiller(
                            playoutBuilderState,
                            scheduleItem,
                            e2,
                            filler.Count.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PreRoll,
                            filler.AllowWatermarks,
                            filler.Id,
                            cancellationToken));
                    break;
                case FillerMode.RandomCount when filler.Count.HasValue:
                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddRandomCountFiller(
                            playoutBuilderState,
                            scheduleItem,
                            e3,
                            filler.Count.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PreRoll,
                            filler.AllowWatermarks,
                            filler.Id,
                            cancellationToken));
                    break;
            }
        }

        if (effectiveChapters.Count <= 1)
        {
            result.Add(playoutItem);
        }
        else
        {
            foreach (FillerPreset filler in allFiller.Filter(f =>
                         f.FillerKind == FillerKind.MidRoll && f.FillerMode != FillerMode.Pad))
            {
                List<MediaChapter> filteredChapters = FillerExpression.FilterChapters(
                    filler.Expression,
                    effectiveChapters,
                    playoutItem);
                if (filteredChapters.Count <= 1)
                {
                    result.Add(playoutItem);
                    continue;
                }

                switch (filler.FillerMode)
                {
                    case FillerMode.Duration when filler.Duration.HasValue:
                        IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                        for (var i = 0; i < filteredChapters.Count; i++)
                        {
                            result.Add(playoutItem.ForChapter(filteredChapters[i]));
                            if (i < filteredChapters.Count - 1)
                            {
                                result.AddRange(
                                    AddDurationFiller(
                                        playoutBuilderState,
                                        e1,
                                        filler.Duration.Value,
                                        scheduleItem.GuideMode == GuideMode.Filler
                                            ? FillerKind.GuideMode
                                            : FillerKind.MidRoll,
                                        filler.AllowWatermarks,
                                        warnings));
                            }
                        }

                        break;
                    case FillerMode.Count when filler.Count.HasValue:
                        IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                        for (var i = 0; i < filteredChapters.Count; i++)
                        {
                            result.Add(playoutItem.ForChapter(filteredChapters[i]));
                            if (i < filteredChapters.Count - 1)
                            {
                                result.AddRange(
                                    AddCountFiller(
                                        playoutBuilderState,
                                        scheduleItem,
                                        e2,
                                        filler.Count.Value,
                                        scheduleItem.GuideMode == GuideMode.Filler
                                            ? FillerKind.GuideMode
                                            : FillerKind.MidRoll,
                                        filler.AllowWatermarks,
                                        filler.Id,
                                        cancellationToken));
                            }
                        }

                        break;
                    case FillerMode.RandomCount when filler.Count.HasValue:
                        IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(filler)];
                        for (var i = 0; i < filteredChapters.Count; i++)
                        {
                            result.Add(playoutItem.ForChapter(filteredChapters[i]));
                            if (i < filteredChapters.Count - 1)
                            {
                                result.AddRange(
                                    AddRandomCountFiller(
                                        playoutBuilderState,
                                        scheduleItem,
                                        e3,
                                        filler.Count.Value,
                                        scheduleItem.GuideMode == GuideMode.Filler
                                            ? FillerKind.GuideMode
                                            : FillerKind.MidRoll,
                                        filler.AllowWatermarks,
                                        filler.Id,
                                        cancellationToken));
                            }
                        }

                        break;
                }
            }
        }

        foreach (FillerPreset filler in allFiller.Filter(f =>
                     f.FillerKind == FillerKind.PostRoll && f.FillerMode != FillerMode.Pad))
        {
            switch (filler.FillerMode)
            {
                case FillerMode.Duration when filler.Duration.HasValue:
                    IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            e1,
                            filler.Duration.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PostRoll,
                            filler.AllowWatermarks,
                            warnings));
                    break;
                case FillerMode.Count when filler.Count.HasValue:
                    IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddCountFiller(
                            playoutBuilderState,
                            scheduleItem,
                            e2,
                            filler.Count.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PostRoll,
                            filler.AllowWatermarks,
                            filler.Id,
                            cancellationToken));
                    break;
                case FillerMode.RandomCount when filler.Count.HasValue:
                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddRandomCountFiller(
                            playoutBuilderState,
                            scheduleItem,
                            e3,
                            filler.Count.Value,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PostRoll,
                            filler.AllowWatermarks,
                            filler.Id,
                            cancellationToken));
                    break;
            }
        }

        // after all non-padded filler has been added, figure out padding
        foreach (FillerPreset padFiller in Optional(
                     allFiller.FirstOrDefault(f => f.FillerMode == FillerMode.Pad && f.PadToNearestMinute.HasValue)))
        {
            var totalDuration = TimeSpan.FromTicks(result.Sum(pi => (pi.Finish - pi.Start).Ticks));

            List<MediaChapter> filteredChapters =
                FillerExpression.FilterChapters(padFiller.Expression, effectiveChapters, playoutItem);

            FillerKind fillerKind = padFiller.FillerKind;
            if (filteredChapters.Count <= 1 && effectiveChapters.Count > 1)
            {
                fillerKind = FillerKind.PostRoll;
            }

            // add primary content to totalDuration only if it hasn't already been added
            if (result.All(pi => pi.MediaItemId != playoutItem.MediaItemId))
            {
                totalDuration += TimeSpan.FromTicks(filteredChapters.Sum(c => (c.EndTime - c.StartTime).Ticks));
            }

            int currentMinute = (playoutItem.StartOffset + totalDuration).Minute;
            // ReSharper disable once PossibleInvalidOperationException
            int targetMinute = (currentMinute + padFiller.PadToNearestMinute.Value - 1) /
                padFiller.PadToNearestMinute.Value * padFiller.PadToNearestMinute.Value;

            DateTimeOffset almostTargetTime = playoutItem.StartOffset + totalDuration -
                                              TimeSpan.FromMinutes(currentMinute) +
                                              TimeSpan.FromMinutes(targetMinute);

            var targetTime = new DateTimeOffset(
                almostTargetTime.Year,
                almostTargetTime.Month,
                almostTargetTime.Day,
                almostTargetTime.Hour,
                almostTargetTime.Minute,
                0,
                almostTargetTime.Offset);

            // ensure filler works for content less than one minute
            if (targetTime <= playoutItem.StartOffset + totalDuration)
            {
                targetTime = targetTime.AddMinutes(padFiller.PadToNearestMinute.Value);
            }

            TimeSpan remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;

            // Logger.LogInformation(
            //     "Total duration {TotalDuration}; need to fill {TimeSpan} to pad properly from {StartTime} to {TargetTime}",
            //     totalDuration,
            //     remainingToFill,
            //     playoutItem.StartOffset,
            //     targetTime);

            switch (fillerKind)
            {
                case FillerKind.PreRoll:
                    IMediaCollectionEnumerator pre1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    result.InsertRange(
                        0,
                        AddDurationFiller(
                            playoutBuilderState,
                            pre1,
                            remainingToFill,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PreRoll,
                            padFiller.AllowWatermarks,
                            warnings));
                    totalDuration = TimeSpan.FromTicks(result.Sum(pi => (pi.Finish - pi.Start).Ticks));
                    remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;
                    if (remainingToFill > TimeSpan.Zero)
                    {
                        result.InsertRange(
                            0,
                            FallbackFillerForPad(
                                playoutBuilderState,
                                enumerators,
                                scheduleItem,
                                remainingToFill,
                                cancellationToken));
                    }

                    break;
                case FillerKind.MidRoll:
                    IMediaCollectionEnumerator mid1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    var fillerQueue = new Queue<PlayoutItem>(
                        AddDurationFiller(
                            playoutBuilderState,
                            mid1,
                            remainingToFill,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.MidRoll,
                            padFiller.AllowWatermarks,
                            warnings));
                    TimeSpan average = filteredChapters.Count <= 1
                        ? remainingToFill
                        : remainingToFill / (filteredChapters.Count - 1);
                    TimeSpan filled = TimeSpan.Zero;

                    // remove post-roll to add after mid-roll/content
                    var postRoll = result.Where(i => i.FillerKind == FillerKind.PostRoll).ToList();
                    result.RemoveAll(i => i.FillerKind == FillerKind.PostRoll);

                    for (var i = 0; i < filteredChapters.Count; i++)
                    {
                        result.Add(playoutItem.ForChapter(filteredChapters[i]));
                        if (i < filteredChapters.Count - 1)
                        {
                            TimeSpan current = TimeSpan.Zero;
                            while (current < average && filled < remainingToFill)
                            {
                                if (fillerQueue.TryDequeue(out PlayoutItem fillerItem))
                                {
                                    result.Add(fillerItem);
                                    current += fillerItem.Finish - fillerItem.Start;
                                    filled += fillerItem.Finish - fillerItem.Start;
                                }
                                else
                                {
                                    TimeSpan leftInThisBreak = average - current;
                                    TimeSpan leftOverall = remainingToFill - filled;

                                    TimeSpan maxThisBreak = leftOverall < leftInThisBreak
                                        ? leftOverall
                                        : leftInThisBreak;

                                    List<PlayoutItem> fallbackItems = FallbackFillerForPad(
                                        playoutBuilderState,
                                        enumerators,
                                        scheduleItem,
                                        i < filteredChapters.Count - 1 ? maxThisBreak : leftOverall,
                                        cancellationToken);

                                    foreach (PlayoutItem fallback in fallbackItems)
                                    {
                                        current += fallback.Finish - fallback.Start;
                                        filled += fallback.Finish - fallback.Start;
                                        result.Add(fallback);
                                    }
                                }
                            }
                        }
                    }

                    result.AddRange(postRoll);

                    break;
                case FillerKind.PostRoll:
                    IMediaCollectionEnumerator post1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            post1,
                            remainingToFill,
                            scheduleItem.GuideMode == GuideMode.Filler ? FillerKind.GuideMode : FillerKind.PostRoll,
                            padFiller.AllowWatermarks,
                            warnings));
                    totalDuration = TimeSpan.FromTicks(result.Sum(pi => (pi.Finish - pi.Start).Ticks));
                    remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;
                    if (remainingToFill > TimeSpan.Zero)
                    {
                        result.AddRange(
                            FallbackFillerForPad(
                                playoutBuilderState,
                                enumerators,
                                scheduleItem,
                                remainingToFill,
                                cancellationToken));
                    }

                    break;
            }
        }

        // fix times on each playout item
        DateTimeOffset currentTime = playoutItem.StartOffset;
        for (var i = 0; i < result.Count; i++)
        {
            PlayoutItem item = result[i];
            TimeSpan duration = item.Finish - item.Start;
            item.Start = currentTime.UtcDateTime;
            item.Finish = (currentTime + duration).UtcDateTime;
            currentTime = item.FinishOffset;
        }

        return result;
    }

    private List<PlayoutItem> AddCountFiller(
        PlayoutBuilderState playoutBuilderState,
        ProgramScheduleItem scheduleItem,
        IMediaCollectionEnumerator enumerator,
        int count,
        FillerKind fillerKind,
        bool allowWatermarks,
        int fillerPresetId,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        for (var i = 0; i < count; i++)
        {
            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
                TimeSpan inPoint = InPointForMediaItem(mediaItem);

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = playoutBuilderState.PlayoutId,
                    MediaItemId = IdForMediaItem(mediaItem),
                    Start = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    Finish = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) + itemDuration,
                    InPoint = inPoint,
                    OutPoint = inPoint + itemDuration,
                    GuideGroup = playoutBuilderState.NextGuideGroup,
                    FillerKind = fillerKind,
                    DisableWatermarks = !allowWatermarks,
                    ChapterTitle = ChapterTitleForMediaItem(mediaItem),
                    SchedulingContext = GetSchedulingContext(scheduleItem, fillerPresetId, enumerator)
                };

                result.Add(playoutItem);

                // TODO: this won't work with reruns
                enumerator.MoveNext(Option<DateTimeOffset>.None);
            }
        }

        return result;
    }

    private static List<PlayoutItem> AddDurationFiller(
        PlayoutBuilderState playoutBuilderState,
        IMediaCollectionEnumerator enumerator,
        TimeSpan duration,
        FillerKind fillerKind,
        bool allowWatermarks,
        PlayoutBuildWarnings warnings)
    {
        var result = new List<PlayoutItem>();

        TimeSpan remainingToFill = duration;
        while (enumerator.Current.IsSome && remainingToFill > TimeSpan.Zero &&
               remainingToFill >= enumerator.MinimumDuration)
        {
            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
                TimeSpan inPoint = InPointForMediaItem(mediaItem);

                if (remainingToFill - itemDuration >= TimeSpan.Zero)
                {
                    var playoutItem = new PlayoutItem
                    {
                        PlayoutId = playoutBuilderState.PlayoutId,
                        MediaItemId = IdForMediaItem(mediaItem),
                        Start = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                        Finish = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) + itemDuration,
                        InPoint = inPoint,
                        OutPoint = inPoint + itemDuration,
                        GuideGroup = playoutBuilderState.NextGuideGroup,
                        FillerKind = fillerKind,
                        DisableWatermarks = !allowWatermarks,
                        ChapterTitle = ChapterTitleForMediaItem(mediaItem)
                    };

                    remainingToFill -= itemDuration;
                    result.Add(playoutItem);

                    // TODO: this won't work with reruns
                    enumerator.MoveNext(Option<DateTimeOffset>.None);
                }
                else
                {
                    warnings.DurationFillerSkipped++;

                    // TODO: this won't work with reruns
                    enumerator.MoveNext(Option<DateTimeOffset>.None);
                }
            }
        }

        return result;
    }

    private static List<PlayoutItem> FallbackFillerForPad(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> enumerators,
        ProgramScheduleItem scheduleItem,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        if (scheduleItem.FallbackFiller != null)
        {
            IMediaCollectionEnumerator enumerator =
                enumerators[CollectionKey.ForFillerPreset(scheduleItem.FallbackFiller)];

            TimeSpan remainingToFill = duration;
            DateTimeOffset currentTime = new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero);

            while (enumerator.Current.IsSome && remainingToFill > TimeSpan.Zero)
            {
                MediaItem mediaItem = enumerator.Current.ValueUnsafe();
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();
                TimeSpan currentDuration = itemDuration < remainingToFill ? itemDuration : remainingToFill;
                TimeSpan inPoint = InPointForMediaItem(mediaItem);

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = playoutBuilderState.PlayoutId,
                    MediaItemId = IdForMediaItem(mediaItem),
                    Start = currentTime.UtcDateTime,
                    Finish = currentTime.Add(currentDuration).UtcDateTime,
                    InPoint = inPoint,
                    OutPoint = inPoint + currentDuration,
                    GuideGroup = playoutBuilderState.NextGuideGroup,
                    FillerKind = FillerKind.Fallback,
                    DisableWatermarks = !scheduleItem.FallbackFiller.AllowWatermarks,
                    ChapterTitle = ChapterTitleForMediaItem(mediaItem)
                };

                result.Add(playoutItem);

                remainingToFill -= currentDuration;
                currentTime += currentDuration;

                // TODO: this won't work with reruns
                enumerator.MoveNext(Option<DateTimeOffset>.None);
            }
        }

        return result;
    }

    private List<PlayoutItem> AddRandomCountFiller(
        PlayoutBuilderState playoutBuilderState,
        ProgramScheduleItem scheduleItem,
        IMediaCollectionEnumerator enumerator,
        int count,
        FillerKind fillerKind,
        bool allowWatermarks,
        int fillerPresetId,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();
        // randomCount is from 0 to count.
        int randomCount = _random.Next(count + 1);

        if (randomCount != 0)
        {
            result = AddCountFiller(
                playoutBuilderState,
                scheduleItem,
                enumerator,
                randomCount,
                fillerKind,
                allowWatermarks,
                fillerPresetId,
                cancellationToken);
        }

        return result;
    }

    protected abstract string SchedulingContextName { get; }

    protected string GetSchedulingContext(
        ProgramScheduleItem scheduleItem,
        int? fillerPresetId,
        IMediaCollectionEnumerator enumerator)
    {
        var context = new ClassicSchedulingContext(
            SchedulingContextName,
            scheduleItem.ProgramScheduleId,
            scheduleItem.Id,
            fillerPresetId,
            enumerator.SchedulingContextName,
            enumerator.State.Seed,
            enumerator.State.Index);

        return JsonSerializer.Serialize(context, Options);
    }
}
