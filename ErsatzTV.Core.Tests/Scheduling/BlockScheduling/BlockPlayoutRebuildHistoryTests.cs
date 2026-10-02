using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.BlockScheduling;
using ErsatzTV.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling.BlockScheduling;

[TestFixture]
public class BlockPlayoutRebuildHistoryTests
{
    private static readonly SmartCollection Collection = new() { Id = 1, Query = "commercials" };

    // 40 commercials with mixed durations, so a changed collection moves every later start time
    private static readonly List<MediaItem> FirstItems = Enumerable.Range(1, 40)
        .Map(i => Commercial(i, new[] { 15, 30, 45 }[i % 3]))
        .ToList<MediaItem>();

    private static readonly List<MediaItem> ChangedItems = FirstItems.Append(Commercial(41, 20)).ToList();

    private Playout _playout;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        var block = new Block
        {
            Id = 1,
            Name = "Test Block",
            Minutes = 30,
            Items = Enumerable.Range(1, 20)
                .Map(i => new BlockItem
                {
                    Id = i,
                    BlockId = 1,
                    CollectionType = CollectionType.SmartCollection,
                    PlaybackOrder = PlaybackOrder.Shuffle,
                    Index = i,
                    SmartCollection = Collection,
                    SmartCollectionId = Collection.Id
                })
                .ToList(),
            StopScheduling = BlockStopScheduling.BeforeDurationEnd
        };

        var template = new Template { Id = 1, Items = [] };
        template.Items.Add(
            new TemplateItem
            {
                Block = block,
                BlockId = block.Id,
                StartTime = TimeSpan.FromHours(20),
                Template = template,
                TemplateId = template.Id
            });

        var playoutTemplate = new PlayoutTemplate
        {
            Id = 1,
            Index = 1,
            Template = template,
            TemplateId = template.Id,
            DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
            DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
            MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear()
        };

        _playout = new Playout
        {
            Id = 1,
            Seed = 12345,
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            Templates = [playoutTemplate],
            Items = [],
            PlayoutHistory = []
        };

        DateTimeOffset midnight = DateTimeOffset.Now - DateTimeOffset.Now.TimeOfDay;
        _now = midnight.AddHours(19);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Rebuild_After_Collection_Change_Should_Not_Repeat_Shuffle_Index(
        CancellationToken cancellationToken)
    {
        PlayoutBuildResult first = await Build(FirstItems, [], [], cancellationToken);

        PlayoutBuildResult second = await Build(
            ChangedItems,
            first.AddedItems,
            first.AddedHistory,
            cancellationToken);

        ShouldHaveConsecutiveIndexes(second);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Rebuild_Should_Remove_Orphaned_History(CancellationToken cancellationToken)
    {
        PlayoutBuildResult first = await Build(FirstItems, [], [], cancellationToken);

        List<PlayoutHistory> history = first.AddedHistory.OrderBy(h => h.When).ToList();

        // an item that was removed without its history
        PlayoutHistory orphan = history[5].Clone();
        orphan.Id = 1001;
        orphan.When += TimeSpan.FromSeconds(1);
        orphan.Index = 2;

        // a second row for a kept item's start time
        PlayoutHistory duplicate = history[8].Clone();
        duplicate.Id = 1002;
        duplicate.Index = 3;

        PlayoutBuildResult second = await Build(
            ChangedItems,
            first.AddedItems,
            [..first.AddedHistory, orphan, duplicate],
            cancellationToken);

        second.HistoryToRemove.ShouldContain(orphan.Id);
        second.HistoryToRemove.ShouldContain(duplicate.Id);
        ShouldHaveConsecutiveIndexes(second);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Rebuild_Should_Keep_History_Before_Build_Start(CancellationToken cancellationToken)
    {
        PlayoutBuildResult first = await Build(FirstItems, [], [], cancellationToken);

        // played items are deleted after they finish, but their history must remain
        PlayoutHistory played = first.AddedHistory.Head().Clone();
        played.Id = 1001;
        played.When = (_now - TimeSpan.FromHours(1)).UtcDateTime;
        played.Finish = played.When + TimeSpan.FromSeconds(30);

        PlayoutBuildResult second = await Build(
            FirstItems,
            first.AddedItems,
            [..first.AddedHistory, played],
            cancellationToken);

        second.HistoryToRemove.ShouldBeEmpty();
    }

    private async Task<PlayoutBuildResult> Build(
        List<MediaItem> collectionItems,
        List<PlayoutItem> existingItems,
        List<PlayoutHistory> existingHistory,
        CancellationToken cancellationToken)
    {
        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
            .Returns(Some(1));

        ICollectionEtag etag = Substitute.For<ICollectionEtag>();
        etag.ForCollectionItems(Arg.Any<List<MediaItem>>())
            .Returns(ci => ci.Arg<List<MediaItem>>().Count.ToString());

        var builder = new BlockPlayoutBuilder(
            configRepo,
            new FakeMediaCollectionRepository(Map((Collection.Id, collectionItems))),
            Substitute.For<ITelevisionRepository>(),
            Substitute.For<IArtistRepository>(),
            etag,
            new LoggerFactory().CreateLogger<BlockPlayoutBuilder>());

        var referenceData = new PlayoutReferenceData(
            _playout.Channel,
            Option<Deco>.None,
            existingItems.ToList(),
            _playout.Templates.ToList(),
            null,
            [],
            existingHistory.ToList(),
            TimeSpan.Zero);

        PlayoutBuildResult result = (await builder.Build(
            _now,
            _playout,
            referenceData,
            PlayoutBuildMode.Refresh,
            cancellationToken)).RightToSeq().Head();

        // assign ids as if the result was saved
        int id = existingItems.Count + existingHistory.Count + 1;
        foreach (PlayoutItem item in result.AddedItems)
        {
            item.Id = id++;
        }

        foreach (PlayoutHistory history in result.AddedHistory)
        {
            history.Id = id++;
        }

        return result;
    }

    private static void ShouldHaveConsecutiveIndexes(PlayoutBuildResult result)
    {
        var indexes = result.AddedHistory.OrderBy(h => h.When).Map(h => h.Index).ToList();
        indexes.ShouldBe(Enumerable.Range(0, indexes.Count).ToList());
    }

    private static Movie Commercial(int id, int seconds) =>
        new()
        {
            Id = id,
            MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today, Title = $"Commercial {id}" }],
            MediaVersions =
            [
                new MediaVersion
                {
                    Duration = TimeSpan.FromSeconds(seconds),
                    MediaFiles = [new MediaFile { Path = $"/fake/path/{id}" }]
                }
            ]
        };
}
