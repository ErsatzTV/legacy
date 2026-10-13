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
public class SequentialPlayoutNestedFillerSequenceTests
{
    [Test]
    [CancelAfter(30_000)]
    public async Task MidRoll_Should_Expand_Nested_Sequences(CancellationToken cancellationToken)
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
                                  - smart_collection: Ad
                                    key: ad
                                    order: shuffle
                                sequence:
                                  - key: up-next
                                    items:
                                      - count: 1
                                        content: ident
                                  - key: break
                                    items:
                                      - count: 1
                                        content: ad
                                      - sequence: up-next
                                        repeat: 2
                                        custom_title: Up Next
                                      - count: 1
                                        content: ad
                                playout:
                                  - mid_roll: true
                                    sequence: break
                                  - count: 1
                                    content: programme
                                  - mid_roll: false
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
                .Returns(call => Task.FromResult(new List<MediaItem> { CreateItem((string)call[0]) }));
            ISequentialScheduleValidator validator = Substitute.For<ISequentialScheduleValidator>();
            validator.ValidateSchedule(Arg.Any<string>(), false).Returns(true);
            var builder = new SequentialPlayoutBuilder(
                fileSystem, config, media, Substitute.For<IChannelRepository>(),
                Substitute.For<IGraphicsElementRepository>(), validator,
                NullLogger<SequentialPlayoutBuilder>.Instance);
            var channel = new Channel(Guid.NewGuid()) { Id = 1, Number = "1", Name = "Nested filler test" };
            var playout = new Playout
            {
                Id = 1, ChannelId = 1, Channel = channel, ScheduleFile = scheduleFile,
                ScheduleKind = PlayoutScheduleKind.Sequential, Seed = 12345,
                Items = [], PlayoutHistory = []
            };
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var reference = new PlayoutReferenceData(
                channel, Option<Deco>.None, [], [], null, [], [], TimeSpan.Zero);

            var result = await builder.Build(start, playout, reference, PlayoutBuildMode.Reset, cancellationToken);

            result.IsRight.ShouldBeTrue();
            PlayoutBuildResult built = result.RightToSeq().Single();
            built.AddedItems.Select(i => i.MediaItemId).ShouldBe([1, 3, 2, 2, 3, 1, 1, 3, 2, 2, 3, 1]);
            built.AddedItems.Where(i => i.MediaItemId == 2).ShouldAllBe(i => i.CustomTitle == "Up Next");
            built.AddedItems.Where(i => i.MediaItemId != 2).ShouldAllBe(i => i.CustomTitle == null);
        }
        finally
        {
            File.Delete(scheduleFile);
        }
    }

    private static Movie CreateItem(string name)
    {
        var version = new MediaVersion
        {
            Duration = name switch
            {
                "Programme" => TimeSpan.FromHours(12),
                _ => TimeSpan.FromSeconds(10)
            }
        };

        if (name == "Programme")
        {
            version.Chapters =
            [
                new MediaChapter { ChapterId = 0, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(6) },
                new MediaChapter
                    { ChapterId = 1, StartTime = TimeSpan.FromHours(6), EndTime = TimeSpan.FromHours(12) }
            ];
        }

        return new Movie
        {
            Id = name switch
            {
                "Programme" => 1,
                "Ident" => 2,
                _ => 3
            },
            MediaVersions = [version],
            MovieMetadata = [new MovieMetadata { Title = name }]
        };
    }
}
