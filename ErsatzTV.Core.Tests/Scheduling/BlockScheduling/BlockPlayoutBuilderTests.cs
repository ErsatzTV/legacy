using Destructurama;
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
using Serilog;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling.BlockScheduling;

public class BlockPlayoutBuilderTests
{
    private readonly ILogger<BlockPlayoutBuilder> _logger;

    public BlockPlayoutBuilderTests()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .Destructure.UsingAttributes()
            .CreateLogger();

        ILoggerFactory loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

        _logger = loggerFactory.CreateLogger<BlockPlayoutBuilder>();
    }

    [TestFixture]
    public class Build : BlockPlayoutBuilderTests
    {
        [Test]
        [CancelAfter(10_000)]
        public async Task Should_Start_At_Beginning_Of_Current_Block(CancellationToken cancellationToken)
        {
            var collection = new SmartCollection
            {
                Id = 1,
                Query = "asdf"
            };

            var block = new Block
            {
                Id = 1,
                Name = "Test Block",
                Minutes = 30,
                Items =
                [
                    new BlockItem
                    {
                        Id = 1,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 1,
                        SmartCollection = collection,
                        SmartCollectionId = collection.Id
                    }
                ],
                StopScheduling = BlockStopScheduling.AfterDurationEnd
            };

            var template = new Template
            {
                Id = 1,
                Items = []
            };

            var templateItem = new TemplateItem
            {
                Block = block,
                BlockId = block.Id,
                StartTime = TimeSpan.FromHours(9),
                Template = template,
                TemplateId = template.Id
            };

            template.Items.Add(templateItem);

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

            var playout = new Playout
            {
                Id = 1,
                Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
                Templates =
                [
                    playoutTemplate
                ],
                Items = [],
                PlayoutHistory = []
            };

            var now = HoursAfterMidnight(9).AddMinutes(15);

            var mediaItems = new List<MediaItem>
            {
                new Movie
                {
                    Id = 1,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromMinutes(25),
                            MediaFiles = [new MediaFile { Path = "/fake/path/1" }]
                        }
                    ]
                }
            };

            var collectionRepo = new FakeMediaCollectionRepository(
                Map((collection.Id, mediaItems))
            );

            IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
            configRepo
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(1));

            var builder = new BlockPlayoutBuilder(
                configRepo,
                collectionRepo,
                Substitute.For<ITelevisionRepository>(),
                Substitute.For<IArtistRepository>(),
                Substitute.For<ICollectionEtag>(),
                _logger);

            var referenceData = new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                playout.Templates.ToList(),
                null,
                [],
                [],
                TimeSpan.Zero);

            Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Reset,
                cancellationToken);

            buildResult.IsRight.ShouldBeTrue();
            foreach (var result in buildResult.RightToSeq())
            {
                // this test only cares about "today"
                result.AddedItems.RemoveAll(i => i.StartOffset.Date > now.Date);

                result.AddedItems.Count.ShouldBe(1);
                result.AddedItems[0].StartOffset.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
            }
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task Should_Discard_Item_That_Will_Never_Fit(CancellationToken cancellationToken)
        {
            var collection = new SmartCollection
            {
                Id = 1,
                Query = "asdf"
            };

            var block = new Block
            {
                Id = 1,
                Name = "Test Block",
                Minutes = 30,
                Items =
                [
                    new BlockItem
                    {
                        Id = 1,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 1,
                        SmartCollection = collection,
                        SmartCollectionId = collection.Id
                    }
                ],
                StopScheduling = BlockStopScheduling.BeforeDurationEnd
            };

            var template = new Template
            {
                Id = 1,
                Items = []
            };

            var templateItem = new TemplateItem
            {
                Block = block,
                BlockId = block.Id,
                StartTime = TimeSpan.FromHours(9),
                Template = template,
                TemplateId = template.Id
            };

            template.Items.Add(templateItem);

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

            var playout = new Playout
            {
                Id = 1,
                Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
                Templates =
                [
                    playoutTemplate
                ],
                Items = [],
                PlayoutHistory = []
            };

            var now = HoursAfterMidnight(9).AddMinutes(15);

            var mediaItems = new List<MediaItem>
            {
                new Movie
                {
                    Id = 1,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromHours(1),
                            MediaFiles = [new MediaFile { Path = "/fake/path/1" }]
                        }
                    ]
                },
                new Movie
                {
                    Id = 2,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today.AddDays(1) }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromMinutes(25),
                            MediaFiles = [new MediaFile { Path = "/fake/path/2" }]
                        }
                    ]
                }
            };

            var collectionRepo = new FakeMediaCollectionRepository(
                Map((collection.Id, mediaItems))
            );

            IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
            configRepo
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(1));

            var builder = new BlockPlayoutBuilder(
                configRepo,
                collectionRepo,
                Substitute.For<ITelevisionRepository>(),
                Substitute.For<IArtistRepository>(),
                Substitute.For<ICollectionEtag>(),
                _logger);

            var referenceData = new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                playout.Templates.ToList(),
                null,
                [],
                [],
                TimeSpan.Zero);

            Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Reset,
                cancellationToken);

            buildResult.IsRight.ShouldBeTrue();
            foreach (var result in buildResult.RightToSeq())
            {
                // this test only cares about "today"
                result.AddedItems.RemoveAll(i => i.StartOffset.Date > now.Date);

                result.AddedItems.Count.ShouldBe(1);
                result.AddedItems[0].StartOffset.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
            }
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task Should_Discard_Collection_That_Will_Never_Fit(CancellationToken cancellationToken)
        {
            var collection = new SmartCollection
            {
                Id = 1,
                Query = "asdf"
            };

            var collection2 = new SmartCollection
            {
                Id = 2,
                Query = "asdf2"
            };

            var block = new Block
            {
                Id = 1,
                Name = "Test Block",
                Minutes = 30,
                Items =
                [
                    new BlockItem
                    {
                        Id = 1,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 1,
                        SmartCollection = collection,
                        SmartCollectionId = collection.Id
                    },
                    new BlockItem
                    {
                        Id = 2,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 2,
                        SmartCollection = collection2,
                        SmartCollectionId = collection2.Id
                    }
                ],
                StopScheduling = BlockStopScheduling.BeforeDurationEnd
            };

            var template = new Template
            {
                Id = 1,
                Items = []
            };

            var templateItem = new TemplateItem
            {
                Block = block,
                BlockId = block.Id,
                StartTime = TimeSpan.FromHours(9),
                Template = template,
                TemplateId = template.Id
            };

            template.Items.Add(templateItem);

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

            var playout = new Playout
            {
                Id = 1,
                Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
                Templates =
                [
                    playoutTemplate
                ],
                Items = [],
                PlayoutHistory = []
            };

            var now = HoursAfterMidnight(9).AddMinutes(15);

            var mediaItems = new List<MediaItem>
            {
                new Movie
                {
                    Id = 1,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromHours(1),
                            MediaFiles = [new MediaFile { Path = "/fake/path/1" }]
                        }
                    ]
                }
            };

            var mediaItems2 = new List<MediaItem>
            {
                new Movie
                {
                    Id = 2,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today.AddDays(1) }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromMinutes(25),
                            MediaFiles = [new MediaFile { Path = "/fake/path/2" }]
                        }
                    ]
                }
            };

            var collectionRepo = new FakeMediaCollectionRepository(
                new Map<int, List<MediaItem>>(
                [
                    (collection.Id, mediaItems),
                    (collection2.Id, mediaItems2)
                ])
            );

            IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
            configRepo
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(1));

            var builder = new BlockPlayoutBuilder(
                configRepo,
                collectionRepo,
                Substitute.For<ITelevisionRepository>(),
                Substitute.For<IArtistRepository>(),
                Substitute.For<ICollectionEtag>(),
                _logger);

            var referenceData = new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                playout.Templates.ToList(),
                null,
                [],
                [],
                TimeSpan.Zero);

            Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Reset,
                cancellationToken);

            buildResult.IsRight.ShouldBeTrue();
            foreach (var result in buildResult.RightToSeq())
            {
                // this test only cares about "today"
                result.AddedItems.RemoveAll(i => i.StartOffset.Date > now.Date);

                result.AddedItems.Count.ShouldBe(1);
                result.AddedItems[0].StartOffset.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
            }
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task Should_Skip_Block_With_Empty_Collection(CancellationToken cancellationToken)
        {
            var collection = new SmartCollection
            {
                Id = 1,
                Query = "asdf"
            };

            var collection2 = new SmartCollection
            {
                Id = 2,
                Query = "asdf2"
            };

            var block = new Block
            {
                Id = 1,
                Name = "Test Block",
                Minutes = 30,
                Items =
                [
                    new BlockItem
                    {
                        Id = 1,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 1,
                        SmartCollection = collection,
                        SmartCollectionId = collection.Id
                    },
                    new BlockItem
                    {
                        Id = 2,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 2,
                        SmartCollection = collection2,
                        SmartCollectionId = collection2.Id
                    }
                ],
                StopScheduling = BlockStopScheduling.BeforeDurationEnd
            };

            var template = new Template
            {
                Id = 1,
                Items = []
            };

            var templateItem = new TemplateItem
            {
                Block = block,
                BlockId = block.Id,
                StartTime = TimeSpan.FromHours(9),
                Template = template,
                TemplateId = template.Id
            };

            template.Items.Add(templateItem);

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

            var playout = new Playout
            {
                Id = 1,
                Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
                Templates =
                [
                    playoutTemplate
                ],
                Items = [],
                PlayoutHistory = []
            };

            var now = HoursAfterMidnight(9).AddMinutes(15);

            var mediaItems = new List<MediaItem>();
            var mediaItems2 = new List<MediaItem>
            {
                new Movie
                {
                    Id = 2,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today.AddDays(1) }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromMinutes(25),
                            MediaFiles = [new MediaFile { Path = "/fake/path/2" }]
                        }
                    ]
                }
            };

            var collectionRepo = new FakeMediaCollectionRepository(
                new Map<int, List<MediaItem>>(
                [
                    (collection.Id, mediaItems),
                    (collection2.Id, mediaItems2)
                ])
            );

            IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
            configRepo
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(1));

            var builder = new BlockPlayoutBuilder(
                configRepo,
                collectionRepo,
                Substitute.For<ITelevisionRepository>(),
                Substitute.For<IArtistRepository>(),
                Substitute.For<ICollectionEtag>(),
                _logger);

            var referenceData = new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                playout.Templates.ToList(),
                null,
                [],
                [],
                TimeSpan.Zero);

            Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Reset,
                cancellationToken);

            buildResult.IsRight.ShouldBeTrue();
            foreach (var result in buildResult.RightToSeq())
            {
                // this test only cares about "today"
                result.AddedItems.RemoveAll(i => i.StartOffset.Date > now.Date);

                result.AddedItems.Count.ShouldBe(1);
                result.AddedItems[0].StartOffset.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
            }
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task RemoveBefore_Must_Not_Move_Into_The_Future(CancellationToken cancellationToken)
        {
            var collection = new SmartCollection { Id = 1, Query = "asdf" };

            var block = new Block
            {
                Id = 1,
                Name = "Test Block",
                Minutes = 50,
                Items =
                [
                    new BlockItem
                    {
                        Id = 1,
                        CollectionType = CollectionType.SmartCollection,
                        PlaybackOrder = PlaybackOrder.Chronological,
                        Index = 1,
                        SmartCollection = collection,
                        SmartCollectionId = collection.Id
                    }
                ],
                StopScheduling = BlockStopScheduling.BeforeDurationEnd
            };

            var template = new Template { Id = 1, Items = [] };

            template.Items.Add(
                new TemplateItem
                {
                    Block = block,
                    BlockId = block.Id,
                    StartTime = TimeSpan.FromHours(8),
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

            var playout = new Playout
            {
                Id = 1,
                Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
                Templates = [playoutTemplate],
                Items = [],
                PlayoutHistory = []
            };

            DateTimeOffset midnight = DateTimeOffset.Now - DateTimeOffset.Now.TimeOfDay;
            DateTimeOffset now = midnight.AddHours(9);

            var mediaItems = new List<MediaItem>
            {
                new Movie
                {
                    Id = 1,
                    MovieMetadata = [new MovieMetadata { ReleaseDate = DateTime.Today }],
                    MediaVersions =
                    [
                        new MediaVersion
                        {
                            Duration = TimeSpan.FromMinutes(25),
                            MediaFiles = [new MediaFile { Path = "/fake/path/1" }]
                        }
                    ]
                }
            };

            var collectionRepo = new FakeMediaCollectionRepository(Map((collection.Id, mediaItems)));

            IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
            configRepo
                .GetValue<int>(Arg.Is(ConfigElementKey.PlayoutDaysToBuild), Arg.Any<CancellationToken>())
                .Returns(Some(1));

            var builder = new BlockPlayoutBuilder(
                configRepo,
                collectionRepo,
                Substitute.For<ITelevisionRepository>(),
                Substitute.For<IArtistRepository>(),
                Substitute.For<ICollectionEtag>(),
                new LoggerFactory().CreateLogger<BlockPlayoutBuilder>());

            var referenceData = new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                playout.Templates.ToList(),
                null,
                [],
                [],
                TimeSpan.Zero);

            Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Continue,
                cancellationToken);

            buildResult.IsRight.ShouldBeTrue();
            foreach (PlayoutBuildResult result in buildResult.RightToSeq())
            {
                DateTimeOffset removeBefore = result.RemoveBefore.IfNone(DateTimeOffset.MinValue);
                await TestContext.Out.WriteLineAsync($"build start  = {now:O}");
                await TestContext.Out.WriteLineAsync($"RemoveBefore = {removeBefore:O}");
                removeBefore.ShouldBeLessThanOrEqualTo(now);
            }
        }
    }



    protected static DateTimeOffset HoursAfterMidnight(int hours)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return now - now.TimeOfDay + TimeSpan.FromHours(hours);
    }
}
