using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class SequentialPlayoutPostRollTests
{
    [Test]
    [CancelAfter(30_000)]
    public async Task Continue_Should_Keep_PostRoll_After_Every_Item(CancellationToken cancellationToken)
    {
        string scheduleFile = Path.GetTempFileName();
        const string schedule = """
                                content:
                                  - smart_collection: Programme
                                    key: programme
                                    order: chronological
                                  - smart_collection: Ident
                                    key: ident
                                    order: shuffle
                                sequence:
                                  - key: ident-break
                                    items:
                                      - count: 1
                                        content: ident
                                        filler_kind: postroll
                                        disable_watermarks: true
                                playout:
                                  - post_roll: true
                                    sequence: ident-break
                                  - count: 2
                                    content: programme
                                  - count: 2
                                    content: programme
                                  - repeat: true
                                """;
        await File.WriteAllTextAsync(scheduleFile, schedule, cancellationToken);
        try
        {
            var fileSystem = new MockFileSystem();
            fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(scheduleFile));
            fileSystem.File.WriteAllText(scheduleFile, schedule);
            IConfigElementRepository config = Substitute.For<IConfigElementRepository>();
            config.GetValue<int>(Arg.Any<ConfigElementKey>(), Arg.Any<CancellationToken>())
                .Returns(Some(1));
            IMediaCollectionRepository media = Substitute.For<IMediaCollectionRepository>();
            media.GetSmartCollectionItemsByName(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new List<MediaItem>
                {
                    new Movie
                    {
                        Id = (string)call[0] == "Ident" ? 2 : 1,
                        MediaVersions = [new MediaVersion
                        {
                            Duration = (string)call[0] == "Ident"
                                ? TimeSpan.FromSeconds(10)
                                : TimeSpan.FromHours(12)
                        }],
                        MovieMetadata = [new MovieMetadata { Title = (string)call[0] }]
                    }
                }));
            ISequentialScheduleValidator validator = Substitute.For<ISequentialScheduleValidator>();
            validator.ValidateSchedule(Arg.Any<string>(), false).Returns(true);
            var builder = new SequentialPlayoutBuilder(
                fileSystem, config, media, Substitute.For<IChannelRepository>(),
                Substitute.For<IGraphicsElementRepository>(), validator,
                NullLogger<SequentialPlayoutBuilder>.Instance);
            var channel = new Channel(Guid.NewGuid()) { Id = 1, Number = "1", Name = "Post-roll test" };
            var playout = new Playout
            {
                Id = 1, ChannelId = 1, Channel = channel, ScheduleFile = scheduleFile,
                ScheduleKind = PlayoutScheduleKind.Sequential, Seed = 12345,
                Items = [], PlayoutHistory = []
            };
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var items = new List<PlayoutItem>();
            var history = new List<PlayoutHistory>();
            for (var build = 0; build < 3; build++)
            {
                var reference = new PlayoutReferenceData(
                    channel, Option<Deco>.None, items, [], null, [], history, TimeSpan.Zero);
                var result = await builder.Build(
                    start.AddDays(build), playout, reference,
                    build == 0 ? PlayoutBuildMode.Reset : PlayoutBuildMode.Continue,
                    cancellationToken);
                result.IsRight.ShouldBeTrue();
                PlayoutBuildResult built = result.RightToSeq().Single();
                built.AddedItems.Select(i => i.MediaItemId).ShouldBe([1, 2, 1, 2],
                    $"build {build} must insert one ident after each programme");
                built.AddedItems.Where(i => i.MediaItemId == 2)
                    .ShouldAllBe(i => i.DisableWatermarks);
                if (build > 0)
                {
                    built.ClearItems.ShouldBeFalse();
                    built.RemoveAfter.IsNone.ShouldBeTrue();
                    built.AddedItems.First().Start.ShouldBe(items.Last().Finish);
                }

                items.AddRange(built.AddedItems);
                history.AddRange(built.AddedHistory);
            }
        }
        finally
        {
            File.Delete(scheduleFile);
        }
    }
}
