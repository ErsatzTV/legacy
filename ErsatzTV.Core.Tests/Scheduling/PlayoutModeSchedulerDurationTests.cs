using Destructurama;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class PlayoutModeSchedulerDurationTests : SchedulerTestBase
{
    [SetUp]
    public void SetUp()
    {
        _cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        _random = new Random();
    }

    private CancellationToken _cancellationToken;
    private Random _random;
    private readonly ILogger<PlayoutModeSchedulerDuration> _logger;

    public PlayoutModeSchedulerDurationTests()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .Destructure.UsingAttributes()
            .CreateLogger();

        ILoggerFactory loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

        _logger = loggerFactory.CreateLogger<PlayoutModeSchedulerDuration>();
    }

    [Test]
    public void Should_Fill_Exact_Duration()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromHours(1));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.None,
            PlaybackOrder = PlaybackOrder.Chronological,
            CustomTitle = "CustomTitle"
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(2); // one guide group here because of custom title
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(3);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();
        playoutItems[0].CustomTitle.ShouldBe("CustomTitle");

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddHours(1));
        playoutItems[1].GuideGroup.ShouldBe(1);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();
        playoutItems[1].CustomTitle.ShouldBe("CustomTitle");

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.AddHours(2));
        playoutItems[2].GuideGroup.ShouldBe(1);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();
        playoutItems[2].CustomTitle.ShouldBe("CustomTitle");
    }

    [Test]
    public void Should_Fill_Exact_Duration_CustomTitle()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromHours(1));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.None,
            PlaybackOrder = PlaybackOrder.Chronological,
            CustomTitle = "Custom Title"
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(2);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(3);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();
        playoutItems[0].CustomTitle.ShouldBe("Custom Title");

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddHours(1));
        playoutItems[1].GuideGroup.ShouldBe(1);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();
        playoutItems[1].CustomTitle.ShouldBe("Custom Title");

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.AddHours(2));
        playoutItems[2].GuideGroup.ShouldBe(1);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();
        playoutItems[2].CustomTitle.ShouldBe("Custom Title");
    }

    [Test]
    public void Should_Not_Have_Gap_Duration_Tail_Mode_None()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.None,
            PlaybackOrder = PlaybackOrder.Chronological
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(3);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();
    }

    [Test]
    public void Should_Have_Gap_Duration_Tail_Mode_Offline_No_Fallback()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.Offline,
            PlaybackOrder = PlaybackOrder.Chronological
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        // duration block should end after exact duration, with gap
        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(3);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);

        // offline should not set guide finish
        playoutItems[2].GuideFinish.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Should_Not_Have_Gap_Duration_Tail_Mode_Offline_With_Fallback()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));
        Collection collectionTwo = TwoItemCollection(3, 4, TimeSpan.FromMinutes(1));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.Offline,
            PlaybackOrder = PlaybackOrder.Chronological,
            FallbackFiller = new FillerPreset
            {
                FillerKind = FillerKind.Fallback,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id
            }
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator1 = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var enumerator2 = new ChronologicalMediaCollectionEnumerator(
            collectionTwo.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator1, scheduleItem.FallbackFiller, enumerator2),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator1.State.Index.ShouldBe(1);
        enumerator2.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(18);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();

        playoutItems[3].MediaItemId.ShouldBe(3);
        playoutItems[3].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));
        playoutItems[3].GuideGroup.ShouldBe(3);
        playoutItems[3].FillerKind.ShouldBe(FillerKind.Fallback);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[17].MediaItemId.ShouldBe(3);
        playoutItems[17].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 59, 0)));
        playoutItems[17].GuideGroup.ShouldBe(3);
        playoutItems[17].FillerKind.ShouldBe(FillerKind.Fallback);
        playoutItems[17].GuideFinish.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Should_Not_Have_Gap_Duration_Tail_Mode_Filler_Exact_Duration()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));
        Collection collectionTwo = TwoItemCollection(3, 4, TimeSpan.FromMinutes(5));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.Filler,
            PlaybackOrder = PlaybackOrder.Chronological,
            TailFiller = new FillerPreset
            {
                FillerKind = FillerKind.Tail,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id
            }
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator1 = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var enumerator2 = new ChronologicalMediaCollectionEnumerator(
            collectionTwo.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator1, scheduleItem.TailFiller, enumerator2),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator1.State.Index.ShouldBe(1);
        enumerator2.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(6);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();

        playoutItems[3].MediaItemId.ShouldBe(3);
        playoutItems[3].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));
        playoutItems[3].GuideGroup.ShouldBe(3);
        playoutItems[3].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[4].MediaItemId.ShouldBe(4);
        playoutItems[4].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 50, 0)));
        playoutItems[4].GuideGroup.ShouldBe(3);
        playoutItems[4].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[5].MediaItemId.ShouldBe(3);
        playoutItems[5].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 55, 0)));
        playoutItems[5].GuideGroup.ShouldBe(3);
        playoutItems[5].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Should_Have_Gap_Duration_Tail_Mode_Filler_No_Fallback()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));
        Collection collectionTwo = TwoItemCollection(3, 4, TimeSpan.FromMinutes(4));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.Filler,
            PlaybackOrder = PlaybackOrder.Chronological,
            TailFiller = new FillerPreset
            {
                FillerKind = FillerKind.Tail,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id
            }
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator1 = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var enumerator2 = new ChronologicalMediaCollectionEnumerator(
            collectionTwo.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator1, scheduleItem.TailFiller, enumerator2),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 57, 0)));

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator1.State.Index.ShouldBe(1);
        enumerator2.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(6);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();

        playoutItems[3].MediaItemId.ShouldBe(3);
        playoutItems[3].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));
        playoutItems[3].GuideGroup.ShouldBe(3);
        playoutItems[3].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[4].MediaItemId.ShouldBe(4);
        playoutItems[4].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 49, 0)));
        playoutItems[4].GuideGroup.ShouldBe(3);
        playoutItems[4].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[4].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[5].MediaItemId.ShouldBe(3);
        playoutItems[5].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 53, 0)));
        playoutItems[5].GuideGroup.ShouldBe(3);
        playoutItems[5].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[5].GuideFinish.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Should_Not_Have_Gap_Duration_Tail_Mode_Filler_With_Fallback()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));
        Collection collectionTwo = TwoItemCollection(3, 4, TimeSpan.FromMinutes(4));
        Collection collectionThree = TwoItemCollection(5, 6, TimeSpan.FromMinutes(1));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(3),
            TailMode = TailMode.Filler,
            PlaybackOrder = PlaybackOrder.Chronological,
            TailFiller = new FillerPreset
            {
                FillerKind = FillerKind.Tail,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id
            },
            FallbackFiller = new FillerPreset
            {
                FillerKind = FillerKind.Fallback,
                Collection = collectionThree,
                CollectionId = collectionThree.Id
            }
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator1 = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var enumerator2 = new ChronologicalMediaCollectionEnumerator(
            collectionTwo.MediaItems,
            new CollectionEnumeratorState());

        var enumerator3 = new ChronologicalMediaCollectionEnumerator(
            collectionThree.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(
                scheduleItem,
                enumerator1,
                scheduleItem.TailFiller,
                enumerator2,
                scheduleItem.FallbackFiller,
                enumerator3),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(3));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        playoutBuilderState.NextGuideGroup.ShouldBe(4);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator1.State.Index.ShouldBe(1);
        enumerator2.State.Index.ShouldBe(1);
        enumerator3.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(9);

        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideGroup.ShouldBe(1);
        playoutItems[0].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[1].MediaItemId.ShouldBe(2);
        playoutItems[1].StartOffset.ShouldBe(startState.CurrentTime.AddMinutes(55));
        playoutItems[1].GuideGroup.ShouldBe(2);
        playoutItems[1].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[1].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[2].MediaItemId.ShouldBe(1);
        playoutItems[2].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(1, 50, 0)));
        playoutItems[2].GuideGroup.ShouldBe(3);
        playoutItems[2].FillerKind.ShouldBe(FillerKind.None);
        playoutItems[2].GuideFinish.HasValue.ShouldBeTrue();

        playoutItems[3].MediaItemId.ShouldBe(3);
        playoutItems[3].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 45, 0)));
        playoutItems[3].GuideGroup.ShouldBe(3);
        playoutItems[3].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[3].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[4].MediaItemId.ShouldBe(4);
        playoutItems[4].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 49, 0)));
        playoutItems[4].GuideGroup.ShouldBe(3);
        playoutItems[4].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[4].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[5].MediaItemId.ShouldBe(3);
        playoutItems[5].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 53, 0)));
        playoutItems[5].GuideGroup.ShouldBe(3);
        playoutItems[5].FillerKind.ShouldBe(FillerKind.Tail);
        playoutItems[5].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[6].MediaItemId.ShouldBe(5);
        playoutItems[6].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 57, 0)));
        playoutItems[6].GuideGroup.ShouldBe(3);
        playoutItems[6].FillerKind.ShouldBe(FillerKind.Fallback);
        playoutItems[6].GuideFinish.HasValue.ShouldBeFalse();

        playoutItems[8].MediaItemId.ShouldBe(5);
        playoutItems[8].StartOffset.ShouldBe(startState.CurrentTime.Add(new TimeSpan(2, 59, 0)));
        playoutItems[8].GuideGroup.ShouldBe(3);
        playoutItems[8].FillerKind.ShouldBe(FillerKind.Fallback);
        playoutItems[8].GuideFinish.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Should_Not_Have_Gap_With_Post_Roll_Pad_And_Fallback_Filler()
    {
        Collection collectionPre = TwoItemCollection(1, 2, TimeSpan.Parse("00:00:15.6734470"));
        Collection collectionOne = TwoItemCollection(3, 4, TimeSpan.Parse("00:22:58.1220000"));
        Collection collectionTwo = CollectionOf(
            new Dictionary<int, TimeSpan>
            {
                { 5, TimeSpan.Parse("00:00:31.3004760") },
                { 6, TimeSpan.Parse("00:00:31.7880950") },
                { 7, TimeSpan.Parse("00:00:31.1147170") },
                { 8, TimeSpan.Parse("00:00:46.4863270") },
                { 9, TimeSpan.Parse("00:00:31.4165760") },
                { 10, TimeSpan.Parse("00:00:31.5791160") },
                { 11, TimeSpan.Parse("00:00:31.2540360") },
                { 12, TimeSpan.Parse("00:00:36.2231070") },
                { 13, TimeSpan.Parse("00:02:00.0471430") }
            });
        Collection collectionThree = TwoItemCollection(14, 15, TimeSpan.Parse("00:00:55.6349890"));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromMinutes(30),
            PlaybackOrder = PlaybackOrder.Chronological,
            PreRollFiller = new FillerPreset
            {
                FillerKind = FillerKind.PreRoll,
                FillerMode = FillerMode.Count,
                Count = 1,
                Collection = collectionPre,
                CollectionId = collectionPre.Id
            },
            PostRollFiller = new FillerPreset
            {
                FillerKind = FillerKind.PostRoll,
                FillerMode = FillerMode.Pad,
                PadToNearestMinute = 30,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id
            },
            FallbackFiller = new FillerPreset
            {
                FillerKind = FillerKind.Fallback,
                Collection = collectionThree,
                CollectionId = collectionThree.Id
            }
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem },
            new CollectionEnumeratorState());

        var enumerator1 = new ChronologicalMediaCollectionEnumerator(
            collectionPre.MediaItems,
            new CollectionEnumeratorState());

        var enumerator2 = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var enumerator3 = new ChronologicalMediaCollectionEnumerator(
            collectionTwo.MediaItems,
            new CollectionEnumeratorState());

        var enumerator4 = new ChronologicalMediaCollectionEnumerator(
            collectionThree.MediaItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(
                scheduleItem,
                enumerator2,
                scheduleItem.PreRollFiller,
                enumerator1,
                scheduleItem.PostRollFiller,
                enumerator3,
                scheduleItem.FallbackFiller,
                enumerator4),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddMinutes(30));
        playoutItems.Last().FinishOffset.ShouldBe(playoutBuilderState.CurrentTime);

        // THIS IS THE KEY TEST - needs to be exactly 30 minutes
        (playoutItems.Last().FinishOffset - playoutItems.First().StartOffset).ShouldBe(TimeSpan.FromMinutes(30));

        // playoutBuilderState.NextGuideGroup.ShouldBe(3);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator1.State.Index.ShouldBe(1);
        enumerator2.State.Index.ShouldBe(1);
        enumerator3.State.Index.ShouldBe(0);
        enumerator4.State.Index.ShouldBe(1);

        playoutItems.Count.ShouldBe(12);
    }

    [Test]
    public void Should_Not_Schedule_At_HardStop()
    {
        Collection collectionOne = TwoItemCollection(1, 2, TimeSpan.FromMinutes(55));

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = TimeSpan.FromHours(6),
            PlaybackOrder = PlaybackOrder.Chronological,
            TailFiller = null,
            FallbackFiller = null,
            PlayoutDuration = TimeSpan.FromHours(1)
        };

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var sortedScheduleItems = new List<ProgramScheduleItem>
        {
            scheduleItem,
            NextScheduleItem
        };

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            sortedScheduleItems,
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        playoutItems.ShouldBeEmpty();

        playoutBuilderState.CurrentTime.ShouldBe(HardStop(scheduleItemsEnumerator));

        playoutBuilderState.NextGuideGroup.ShouldBe(1);
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.InFlood.ShouldBeFalse();
        playoutBuilderState.MultipleRemaining.IsNone.ShouldBeTrue();
        playoutBuilderState.InDurationFiller.ShouldBeFalse();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(0);

        enumerator.State.Index.ShouldBe(0);
    }

    [Test]
    public void Should_Stop_At_Duration_When_Discard_To_Fill_Attempts_Exhausted()
    {
        Collection collectionOne = CollectionOf(
            new Dictionary<int, TimeSpan>
            {
                { 1, TimeSpan.FromMinutes(40) },
                { 2, TimeSpan.FromMinutes(40) },
                { 3, TimeSpan.FromMinutes(40) },
                { 4, TimeSpan.FromMinutes(15) }
            });

        var scheduleItem = new ProgramScheduleItemDuration
        {
            Id = 1,
            Index = 1,
            Collection = collectionOne,
            CollectionId = collectionOne.Id,
            StartTime = null,
            PlayoutDuration = TimeSpan.FromHours(1),
            TailMode = TailMode.Offline,
            DiscardToFillAttempts = 1,
            PlaybackOrder = PlaybackOrder.Chronological,
            TailFiller = null,
            FallbackFiller = null
        };

        var enumerator = new ChronologicalMediaCollectionEnumerator(
            collectionOne.MediaItems,
            new CollectionEnumeratorState());

        var scheduleItemsEnumerator = new OrderedScheduleItemsEnumerator(
            new List<ProgramScheduleItem> { scheduleItem, NextScheduleItem },
            new CollectionEnumeratorState());

        PlayoutBuilderState startState = StartState(scheduleItemsEnumerator);

        var scheduler = new PlayoutModeSchedulerDuration(_logger);
        (PlayoutBuilderState playoutBuilderState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings _) = scheduler.Schedule(
            startState,
            CollectionEnumerators(scheduleItem, enumerator),
            scheduleItem,
            NextScheduleItem,
            HardStop(scheduleItemsEnumerator),
            _random,
            _cancellationToken);

        // item 1 fits; items 2 and 3 are too long for the remaining 20 minutes, which exhausts the single attempt
        playoutItems.Count.ShouldBe(1);
        playoutItems[0].MediaItemId.ShouldBe(1);
        playoutItems[0].StartOffset.ShouldBe(startState.CurrentTime);
        playoutItems[0].GuideFinish.HasValue.ShouldBeFalse();

        playoutBuilderState.CurrentTime.ShouldBe(startState.CurrentTime.AddHours(1));
        playoutBuilderState.DurationFinish.IsNone.ShouldBeTrue();
        playoutBuilderState.ScheduleItemsEnumerator.State.Index.ShouldBe(1);

        enumerator.State.Index.ShouldBe(2);
    }
}
