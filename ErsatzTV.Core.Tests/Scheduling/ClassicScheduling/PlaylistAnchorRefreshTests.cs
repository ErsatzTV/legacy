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

// refresh used to re-pick one anchor per collection with a bucket per key column, and there was no
// bucket for playlists, so every playlist anchor was dropped on every refresh
[TestFixture]
public class PlaylistAnchorRefreshTests : PlayoutBuilderTestBase
{
    private const int PlaylistId = 1;
    private const int DaysToBuild = 2;

    [Test]
    public async Task Refresh_Should_Not_Reset_Playlist_Progress()
    {
        // long enough that the playlist cycle can't complete, so the index measures progress directly
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            PlaylistTestData(TimeSpan.FromHours(6), itemCount: 300);

        DateTimeOffset start = LocalTime(20);

        for (var hour = 0; hour < 24 * 21; hour++)
        {
            await builder.Build(
                start.AddHours(hour),
                playout,
                referenceData,
                PlayoutBuildMode.Continue,
                CancellationToken);
        }

        int before = HeadIndex(playout);
        before.ShouldBeGreaterThan(0, "the fast forward should have consumed some of the playlist");

        Either<BaseError, PlayoutBuildResult> result = await builder.Build(
            start.AddDays(21),
            playout,
            referenceData,
            PlayoutBuildMode.Refresh,
            CancellationToken);

        result.IsRight.ShouldBeTrue();

        // rewinding to the start of today legitimately moves the index back by up to a day; a refresh
        // that dropped the anchor lands back at the beginning of the playlist
        int after = HeadIndex(playout);
        after.ShouldBeGreaterThan(
            before / 2,
            $"[tz {TimeZoneInfo.Local.Id}] refresh reset playlist progress (was {before}, now {after})");
    }

    private static int HeadIndex(Playout playout) =>
        playout.ProgramScheduleAnchors
            .Filter(a => a.AnchorDate is null && a.PlaylistId == PlaylistId)
            .Map(a => a.EnumeratorState.Index)
            .HeadOrNone()
            .IfNone(-1);

    private (PlayoutBuilder, Playout, PlayoutReferenceData) PlaylistTestData(TimeSpan itemDuration, int itemCount)
    {
        List<MediaItem> mediaItems = Enumerable.Range(1, itemCount)
            .Map(i => (MediaItem)TestMovie(i, itemDuration, new DateTime(2000 + i, 1, 1)))
            .ToList();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    Index = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = true,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                mediaItems
            }
        };

        var collectionRepo = new FakeMediaCollectionRepository(
            Map((1, mediaItems)),
            Map((PlaylistId, playlistItemMap)));

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<int>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutDaysToBuild.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(DaysToBuild));

        var builder = new PlayoutBuilder(
            configRepo,
            collectionRepo,
            new FakeTelevisionRepository(),
            Substitute.For<IArtistRepository>(),
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>(),
            new MockFileSystem(),
            Substitute.For<IRerunHelper>(),
            Logger);

        var schedule = new ProgramSchedule
        {
            Id = 1,
            Items =
            [
                new ProgramScheduleItemFlood
                {
                    Id = 1,
                    Index = 1,
                    CollectionType = CollectionType.Playlist,
                    PlaylistId = PlaylistId,
                    StartTime = null,
                    PlaybackOrder = PlaybackOrder.Chronological
                }
            ]
        };

        var playout = new Playout
        {
            Id = 1,
            ScheduleKind = PlayoutScheduleKind.Classic,
            ProgramSchedule = schedule,
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            Items = [],
            ProgramScheduleAnchors = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData = new PlayoutReferenceData(
            playout.Channel,
            Option<Deco>.None,
            [],
            [],
            schedule,
            [],
            [],
            TimeSpan.Zero);

        return (builder, playout, referenceData);
    }

    // anchor dates are compared in machine-local time, so the test clock has to be local too; mid-June
    // to stay clear of DST transitions in every zone
    private static DateTimeOffset LocalTime(int hour)
    {
        var date = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)) + TimeSpan.FromHours(hour);
    }
}
