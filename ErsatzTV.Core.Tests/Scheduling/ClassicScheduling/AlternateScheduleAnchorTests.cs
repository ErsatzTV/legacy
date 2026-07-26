using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Tests.Fakes;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.Scheduling.ClassicScheduling;

/// <summary>
///     A collection used only by an alternate schedule still gets an enumerator on days when that
///     alternate isn't active, because the collection media items are gathered once for the primary
///     schedule and every alternate. On those days it falls into the filler branch and gets a
///     shuffled enumerator, whose index domain is the grouped item count rather than the media item
///     count.
/// </summary>
[TestFixture]
public class AlternateScheduleAnchorTests : PlayoutBuilderTestBase
{
    private const int PrimaryCollectionId = 1;
    private const int AlternateCollectionId = 2;

    /// <summary>
    ///     The alternate's collection has 10 episodes and its schedule orders them by season/episode,
    ///     so its own enumerator's index domain is 0-9. With a multi-part pair the shuffled enumerator
    ///     used on inactive days groups those 10 items into 9, so index 9 is out of its domain.
    /// </summary>
    [TestCase(9, true, ExpectedResult = 9, TestName = "Index at the grouped count")]
    [TestCase(8, true, ExpectedResult = 8, TestName = "Index below the grouped count")]
    [TestCase(9, false, ExpectedResult = 9, TestName = "Index at the grouped count, nothing grouped")]
    public async Task<int> Inactive_Alternate_Collection_Should_Keep_Its_Progress(int index, bool multiPart)
    {
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            AlternateTestData(multiPart);

        playout.ProgramScheduleAnchors.Add(
            new PlayoutProgramScheduleAnchor
            {
                Playout = playout,
                PlayoutId = playout.Id,
                CollectionType = CollectionType.Collection,
                CollectionId = AlternateCollectionId,
                EnumeratorState = new CollectionEnumeratorState { Seed = 1234, Index = index }
            });

        // a Wednesday, so the alternate (Sundays only) is not active and the alternate's
        // collection is not scheduled at all
        DateTimeOffset now = LocalTime(new DateTime(2025, 6, 11), 20);
        now.DayOfWeek.ShouldBe(DayOfWeek.Wednesday);

        Either<BaseError, PlayoutBuildResult> result = await builder.Build(
            now,
            playout,
            referenceData,
            PlayoutBuildMode.Continue,
            CancellationToken);

        result.IsRight.ShouldBeTrue();

        return HeadIndex(playout, AlternateCollectionId);
    }

    private static int HeadIndex(Playout playout, int collectionId) =>
        playout.ProgramScheduleAnchors
            .Filter(a => a.AnchorDate is null && a.CollectionId == collectionId)
            .Map(a => a.EnumeratorState.Index)
            .HeadOrNone()
            .IfNone(-1);

    private (PlayoutBuilder, Playout, PlayoutReferenceData) AlternateTestData(bool multiPart)
    {
        var primaryCollection = new Collection
        {
            Id = PrimaryCollectionId,
            Name = "Primary Collection",
            MediaItems = Enumerable.Range(1, 10)
                .Map(i => (MediaItem)TestMovie(i, TimeSpan.FromHours(1), new DateTime(2000 + i, 1, 1)))
                .ToList()
        };

        var season = new Season { Id = 1, ShowId = 1, SeasonNumber = 1 };
        var alternateCollection = new Collection
        {
            Id = AlternateCollectionId,
            Name = "Alternate Collection",
            MediaItems = Enumerable.Range(1, 10)
                .Map(i => (MediaItem)new Episode
                {
                    Id = 100 + i,
                    Season = season,
                    SeasonId = season.Id,
                    EpisodeMetadata =
                    [
                        new EpisodeMetadata
                        {
                            EpisodeNumber = i,
                            // episodes 4 and 5 are a two-parter, so they group into one
                            Title = (multiPart, i) switch
                            {
                                (true, 4) => "Episode Four (1)",
                                (true, 5) => "Episode Five (2)",
                                _ => $"Episode {i}"
                            }
                        }
                    ],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromHours(1),
                            MediaFiles = [new MediaFile { Path = $"/fake/path/episode/{i}" }],
                            Chapters = []
                        }
                    ]
                })
                .ToList()
        };

        var collectionRepo = new FakeMediaCollectionRepository(
            Map(
                (primaryCollection.Id, primaryCollection.MediaItems.ToList()),
                (alternateCollection.Id, alternateCollection.MediaItems.ToList())));

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<int>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutDaysToBuild.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(1));

        var builder = new PlayoutBuilder(
            configRepo,
            collectionRepo,
            new FakeTelevisionRepository(),
            Substitute.For<IArtistRepository>(),
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>(),
            new MockFileSystem(),
            Substitute.For<IRerunHelper>(),
            Logger);

        var primarySchedule = new ProgramSchedule
        {
            Id = 1,
            KeepMultiPartEpisodesTogether = true,
            Items =
            [
                new ProgramScheduleItemFlood
                {
                    Id = 1,
                    Index = 1,
                    CollectionType = CollectionType.Collection,
                    Collection = primaryCollection,
                    CollectionId = primaryCollection.Id,
                    StartTime = null,
                    PlaybackOrder = PlaybackOrder.Chronological
                }
            ]
        };

        var alternateSchedule = new ProgramSchedule
        {
            Id = 2,
            KeepMultiPartEpisodesTogether = true,
            Items =
            [
                new ProgramScheduleItemFlood
                {
                    Id = 2,
                    Index = 1,
                    CollectionType = CollectionType.Collection,
                    Collection = alternateCollection,
                    CollectionId = alternateCollection.Id,
                    StartTime = null,
                    PlaybackOrder = PlaybackOrder.SeasonEpisode
                }
            ]
        };

        var playout = new Playout
        {
            Id = 1,
            ScheduleKind = PlayoutScheduleKind.Classic,
            ProgramSchedule = primarySchedule,
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            Items = [],
            ProgramScheduleAnchors = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var alternates = new List<ProgramScheduleAlternate>
        {
            new()
            {
                Id = 1,
                Index = 1,
                PlayoutId = playout.Id,
                ProgramSchedule = alternateSchedule,
                ProgramScheduleId = alternateSchedule.Id,
                DaysOfWeek = [DayOfWeek.Sunday],
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear()
            }
        };

        var referenceData = new PlayoutReferenceData(
            playout.Channel,
            Option<Deco>.None,
            [],
            [],
            primarySchedule,
            alternates,
            [],
            TimeSpan.Zero);

        return (builder, playout, referenceData);
    }

    private static DateTimeOffset LocalTime(DateTime date, int hour) =>
        new DateTimeOffset(
            DateTime.SpecifyKind(date, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(date)) + TimeSpan.FromHours(hour);
}
