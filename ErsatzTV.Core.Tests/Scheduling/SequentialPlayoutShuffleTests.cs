using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class SequentialPlayoutShuffleTests
{
    private const int SequenceLength = 20;

    [Test]
    public async Task Shuffle_Should_Preserve_Nested_Sequence_Positions()
    {
        var shuffle = new YamlPlayoutShuffleSequenceInstruction { ShuffleSequence = "outer" };
        var outerGuid = Guid.NewGuid();
        var first = new YamlPlayoutInstruction
        {
            Content = "first",
            SequenceKey = "outer",
            SequenceGuid = outerGuid
        };
        var nested = new YamlPlayoutInstruction
        {
            Content = "nested",
            SequenceKey = "inner",
            SequenceGuid = Guid.NewGuid()
        };
        var second = new YamlPlayoutInstruction
        {
            Content = "second",
            SequenceKey = "outer",
            SequenceGuid = outerGuid
        };
        var definition = new YamlPlayoutDefinition { Playout = [shuffle, first, nested, second] };
        var context = new YamlPlayoutContext(new Playout(), definition, 1);
        var handler = new YamlPlayoutShuffleSequenceHandler();

        bool result = await handler.Handle(
            context,
            shuffle,
            PlayoutBuildMode.Reset,
            _ => Task.CompletedTask,
            NullLogger<SequentialPlayoutBuilder>.Instance,
            CancellationToken.None);

        result.ShouldBeTrue();
        definition.Playout.Count.ShouldBe(4);
        definition.Playout[2].ShouldBeSameAs(nested);
        definition.Playout.Where(i => i.SequenceKey == "outer").Select(i => i.Content).Order()
            .ShouldBe(["first", "second"]);
    }

    [Test]
    public void Shuffle_Should_Give_Up_When_Every_Draw_Starts_With_The_Tail()
    {
        // a group of repeated objects can never draw a head that differs from the tail by reference
        (YamlPlayoutDefinition definition, YamlPlayoutShuffleSequenceInstruction shuffle) =
            CreateRepeatedInstructionDefinition();
        var handler = new YamlPlayoutShuffleSequenceHandler();

        Task<bool> handle = Task.Run(() => handler.Handle(
            new YamlPlayoutContext(new Playout(), definition, 1),
            shuffle,
            PlayoutBuildMode.Reset,
            _ => Task.CompletedTask,
            NullLogger<SequentialPlayoutBuilder>.Instance,
            CancellationToken.None));

        handle.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the shuffle must not retry forever");
        handle.Result.ShouldBeTrue();
        definition.Playout.Count.ShouldBe(4);
    }

    [Test]
    public void Shuffle_Should_Stop_Retrying_When_Cancelled()
    {
        (YamlPlayoutDefinition definition, YamlPlayoutShuffleSequenceInstruction shuffle) =
            CreateRepeatedInstructionDefinition();
        var handler = new YamlPlayoutShuffleSequenceHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task<bool> handle = Task.Run(() => handler.Handle(
            new YamlPlayoutContext(new Playout(), definition, 1),
            shuffle,
            PlayoutBuildMode.Reset,
            _ => Task.CompletedTask,
            NullLogger<SequentialPlayoutBuilder>.Instance,
            cts.Token));

        handle.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("a cancelled build must interrupt the shuffle");
        handle.Result.ShouldBeTrue();
    }

    private static (YamlPlayoutDefinition Definition, YamlPlayoutShuffleSequenceInstruction Shuffle)
        CreateRepeatedInstructionDefinition()
    {
        var shuffle = new YamlPlayoutShuffleSequenceInstruction { ShuffleSequence = "shows" };
        var repeated = new YamlPlayoutInstruction
        {
            Content = "show",
            SequenceKey = "shows",
            SequenceGuid = Guid.NewGuid()
        };

        return (new YamlPlayoutDefinition { Playout = [shuffle, repeated, repeated, repeated] }, shuffle);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Continue_Should_Resume_The_Saved_Shuffled_Order(CancellationToken cancellationToken)
    {
        string scheduleFile = Path.GetTempFileName();
        string schedule = BuildSchedule();
        await File.WriteAllTextAsync(scheduleFile, schedule, cancellationToken);
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(scheduleFile));
        fileSystem.File.WriteAllText(scheduleFile, schedule);

        try
        {
            IConfigElementRepository config = Substitute.For<IConfigElementRepository>();
            config
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(2));

            Dictionary<string, MediaItem> mediaItems = Enumerable.Range(1, SequenceLength).ToDictionary(
                i => $"Show {i:00}",
                i => (MediaItem)new Movie
                {
                    Id = i,
                    MediaVersions = [new MediaVersion { Duration = TimeSpan.FromHours(1) }],
                    MovieMetadata =
                    [
                        new MovieMetadata
                        {
                            Title = $"Show {i:00}",
                            ReleaseDate = new DateTime(2000, 1, i)
                        }
                    ]
                });

            IMediaCollectionRepository media = Substitute.For<IMediaCollectionRepository>();
            media
                .GetSmartCollectionItemsByName(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new List<MediaItem> { mediaItems[(string)call[0]] }));

            ISequentialScheduleValidator validator = Substitute.For<ISequentialScheduleValidator>();
            validator.ValidateSchedule(Arg.Any<string>(), false).Returns(true);

            var builder = new SequentialPlayoutBuilder(
                fileSystem,
                config,
                media,
                Substitute.For<IChannelRepository>(),
                Substitute.For<IGraphicsElementRepository>(),
                validator,
                NullLogger<SequentialPlayoutBuilder>.Instance);

            var channel = new Channel(Guid.NewGuid()) { Id = 1, Number = "1", Name = "Shuffle test" };
            var playout = new Playout
            {
                Id = 1,
                ChannelId = channel.Id,
                Channel = channel,
                ScheduleFile = scheduleFile,
                ScheduleKind = PlayoutScheduleKind.Sequential,
                Seed = 12345,
                Items = [],
                PlayoutHistory = []
            };
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            PlayoutBuildResult first = await Build(
                builder,
                start,
                playout,
                channel,
                [],
                [],
                PlayoutBuildMode.Reset,
                cancellationToken);

            YamlPlayoutContext.State savedState =
                JsonConvert.DeserializeObject<YamlPlayoutContext.State>(playout.Anchor.Context);
            savedState.InstructionIndex.ShouldBe(9);
            savedState.SequenceOrders.ShouldContainKey(string.Empty);
            List<int> savedOrder = savedState.SequenceOrders[string.Empty].Single().Order;

            PlayoutBuildResult second = await Build(
                builder,
                start.AddDays(2),
                playout,
                channel,
                first.AddedItems,
                first.AddedHistory,
                PlayoutBuildMode.Continue,
                cancellationToken);

            AssertStartsWithSavedRemainder(second, savedState, savedOrder);

            YamlPlayoutContext.State secondSavedState =
                JsonConvert.DeserializeObject<YamlPlayoutContext.State>(playout.Anchor.Context);
            List<int> secondSavedOrder = secondSavedState.SequenceOrders[string.Empty].Single().Order;
            PlayoutBuildResult third = await Build(
                builder,
                start.AddDays(4),
                playout,
                channel,
                second.AddedItems,
                first.AddedHistory.Concat(second.AddedHistory).ToList(),
                PlayoutBuildMode.Continue,
                cancellationToken);
            AssertStartsWithSavedRemainder(third, secondSavedState, secondSavedOrder);

            List<int> declarationOrder = Enumerable.Range(1, SequenceLength).ToList();
            List<int> combined = first.AddedItems
                .Concat(second.AddedItems)
                .Concat(third.AddedItems)
                .OrderBy(i => i.Start)
                .Select(i => i.MediaItemId)
                .ToList();
            foreach (int[] rotation in combined.Chunk(SequenceLength).Where(chunk => chunk.Length == SequenceLength))
            {
                rotation.Order().ShouldBe(declarationOrder);
            }
        }
        finally
        {
            File.Delete(scheduleFile);
        }
    }

    private static void AssertStartsWithSavedRemainder(
        PlayoutBuildResult result,
        YamlPlayoutContext.State savedState,
        List<int> savedOrder)
    {
        List<int> expectedRemainder = savedOrder
            .Skip(savedState.InstructionIndex.Value - 1)
            .Select(index => index + 1)
            .ToList();
        result.AddedItems
            .OrderBy(i => i.Start)
            .Take(expectedRemainder.Count)
            .Select(i => i.MediaItemId)
            .ShouldBe(expectedRemainder);
    }

    private static async Task<PlayoutBuildResult> Build(
        SequentialPlayoutBuilder builder,
        DateTimeOffset start,
        Playout playout,
        Channel channel,
        List<PlayoutItem> existingItems,
        List<PlayoutHistory> history,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        var referenceData = new PlayoutReferenceData(
            channel,
            Option<Deco>.None,
            existingItems,
            [],
            null,
            [],
            history,
            TimeSpan.Zero);

        var buildResult = await builder.Build(start, playout, referenceData, mode, cancellationToken);
        buildResult.IsRight.ShouldBeTrue();
        return buildResult.RightToSeq().Single();
    }

    private static string BuildSchedule()
    {
        var yaml = new System.Text.StringBuilder("content:\n");
        for (var i = 1; i <= SequenceLength; i++)
        {
            yaml.AppendLine($"  - smart_collection: Show {i:00}");
            yaml.AppendLine($"    key: show-{i:00}");
            yaml.AppendLine("    order: chronological");
        }

        yaml.AppendLine("sequence:");
        yaml.AppendLine("  - key: shows");
        yaml.AppendLine("    items:");
        for (var i = 1; i <= SequenceLength; i++)
        {
            yaml.AppendLine("      - count: 1");
            yaml.AppendLine($"        content: show-{i:00}");
        }

        yaml.AppendLine("playout:");
        yaml.AppendLine("  - shuffle_sequence: shows");
        yaml.AppendLine("  - sequence: shows");
        yaml.AppendLine("  - repeat: true");
        return yaml.ToString();
    }
}
