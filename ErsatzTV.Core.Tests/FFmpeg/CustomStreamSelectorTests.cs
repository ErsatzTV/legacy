using Destructurama;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Shouldly;
using Testably.Abstractions.Testing;
using TimeZoneConverter;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class CustomStreamSelectorTests
{
    [TestFixture]
    public class SelectStreams
    {
        private readonly ILogger<CustomStreamSelector> _logger;

        public SelectStreams()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .Destructure.UsingAttributes()
                .CreateLogger();

            ILoggerFactory loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

            _logger = loggerFactory.CreateLogger<CustomStreamSelector>();
        }

        [SetUp]
        public void SetUp()
        {
            _channel = new Channel(Guid.Empty)
            {
                StreamSelectorMode = ChannelStreamSelectorMode.Custom,
                StreamSelector = TestFileName
            };

            _audioVersion = GetTestAudioVersion("eng");

            _subtitles =
            [
                new Subtitle
                {
                    Id = 1,
                    Language = "eng",
                    Title = "Words",
                    SubtitleKind = SubtitleKind.Embedded,
                    IsExtracted = false,
                    Codec = "srt"
                },
                new Subtitle
                {
                    Id = 2,
                    Language = "eng",
                    Title = "Words",
                    SubtitleKind = SubtitleKind.Embedded,
                    IsExtracted = true,
                    Codec = "srt"
                },
                new Subtitle
                {
                    Id = 3, Language = "en", Title = "Signs", SubtitleKind = SubtitleKind.Embedded, IsExtracted = true
                },
                new Subtitle
                {
                    Id = 4, Language = "en", Title = "Songs", SubtitleKind = SubtitleKind.Embedded, IsExtracted = true
                },
                new Subtitle { Id = 5, Language = "en", Forced = true, SubtitleKind = SubtitleKind.Sidecar },
                new Subtitle { Id = 6, Language = "jp", SubtitleKind = SubtitleKind.Embedded, IsExtracted = true }
            ];
        }

        private static readonly string TestFileName = Path.Combine(
            FileSystemLayout.ChannelStreamSelectorsFolder,
            "test.yml");

        private Channel _channel;
        private MediaItemAudioVersion _audioVersion;
        private List<Subtitle> _subtitles;

        [Test]
        public async Task Should_Select_eng_Audio_Exact_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "eng"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_und_Audio_Missing_Language()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["und"]
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(3);
                audioStream.Language.ShouldBeNullOrWhiteSpace();
            }
        }

        [Test]
        public async Task Should_Select_eng_Audio_Exact_Match_Multiple_Audio_Languages()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["en", "eng"]
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_eng_Audio_Exact_Match_Multiple_Items()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "de"
                    subtitle_language:
                    - "eng"
                  - audio_language:
                    - "eng"
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_eng_Audio_Pattern_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_en_Audio_Pattern_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                """;
            _audioVersion = GetTestAudioVersion("en");

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task disable_subtitles_Should_Select_No_Subtitles()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "eng"
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task Should_Select_eng_Subtitle_Exact_Match_Extracted()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "ja"
                    subtitle_language:
                    - "eng"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_eng_Subtitle_Pattern_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "ja"
                    subtitle_language:
                    - "en*"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_en_Subtitle_Pattern_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "ja"
                    subtitle_language:
                    - "en*"
                """;
            _audioVersion = GetTestAudioVersion("en");

            _subtitles =
            [
                new Subtitle
                {
                    Id = 1, Language = "en", Title = "Words", SubtitleKind = SubtitleKind.Embedded, IsExtracted = true
                }
            ];

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(1);
                subtitle.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task Should_Select_No_Subtitle_Exact_Match_Multiple_Items()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "de"
                    subtitle_language:
                    - "eng"
                  - audio_language:
                    - "eng"
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task Should_Select_Foreign_Audio_And_English_Subtitle_Multiple_Items()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "ja"
                    subtitle_language:
                    - "eng"
                  - audio_language:
                    - "eng"
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(0);
                audioStream.Language.ShouldBe("ja");
            }

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_English_Audio_No_Subtitles_Time_Of_Day_Content_Condition_Fail()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "time_of_day_seconds >= 43200"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now.LocalDateTime.Date.AddHours(11).AddMinutes(59), // 11:59 AM
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task Should_Select_Foreign_Audio_And_English_Subtitle_Time_Of_Day_Content_Condition_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "time_of_day_seconds < 43200"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now.LocalDateTime.Date.AddHours(11).AddMinutes(59), // 11:59 AM
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(0);
                audioStream.Language.ShouldBe("ja");
            }

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Content_Condition_Fail()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 1"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Unspecified); // sunday
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Content_Condition_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 0"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Unspecified); // sunday
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(0);
                audioStream.Language.ShouldBe("ja");
            }

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task
            Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Time_Of_Day_Content_Condition_Fail_Before()
        {
            // saturday from 9pm-11pm
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 6 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 10, 20, 59, 59, DateTimeKind.Unspecified); // saturday at 8:59:59pm
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task
            Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Time_Of_Day_Content_Condition_Fail_After()
        {
            // saturday from 9pm-11pm
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 6 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 10, 23, 0, 0, DateTimeKind.Unspecified); // saturday at 11:00pm
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task
            Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Time_Of_Day_Content_Condition_Fail_Wrong_Day()
        {
            // saturday from 9pm-11pm
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 6 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 11, 22, 0, 0, DateTimeKind.Unspecified); // sunday at 10:00pm
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(1);
                audioStream.Language.ShouldBe("eng");
            }

            result.Subtitle.IsSome.ShouldBeFalse();
        }

        [Test]
        public async Task Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Time_Of_Day_Content_Condition_Match()
        {
            // saturday from 9pm-11pm
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 6 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 10, 22, 0, 0, DateTimeKind.Unspecified); // saturday at 10:00pm
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(0);
                audioStream.Language.ShouldBe("ja");
            }

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        [SetCulture("fr-FR")]
        public async Task
            Should_Select_English_Audio_No_Subtitles_Day_Of_Week_Time_Of_Day_Content_Condition_Match_France()
        {
            // saturday from 9pm-11pm
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["ja"]
                    subtitle_language: ["eng"]
                    content_condition: "day_of_week = 5 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)"

                  - audio_language: ["eng"]
                    disable_subtitles: true
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            var tz = TZConvert.GetTimeZoneInfo("America/Chicago");
            var start = new DateTime(2026, 1, 10, 22, 0, 0, DateTimeKind.Unspecified); // saturday at 10:00pm
            var dto = new DateTimeOffset(start, tz.GetUtcOffset(start));

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                dto,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(0);
                audioStream.Language.ShouldBe("ja");
            }

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(2);
                subtitle.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Ignore_Blocked_Audio_Title()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                    audio_title_blocklist:
                    - "riff"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(2);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_Allowed_Audio_Title()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                    audio_title_allowlist:
                    - "movie"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(2);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Ignore_Blocked_Subtitle_Title()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "*"
                    subtitle_language:
                    - "en"
                    subtitle_title_blocklist:
                    - "signs"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(4);
                subtitle.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task Should_Select_Allowed_Subtitle_Title()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "*"
                    subtitle_language:
                    - "en"
                    subtitle_title_allowlist:
                    - "songs"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(4);
                subtitle.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task Should_Select_Condition_Forced_Subtitle()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "*"
                    subtitle_condition: "forced"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(5);
                subtitle.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task Should_Select_Condition_External_Subtitle()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "*"
                    subtitle_condition: "lang like 'en%' and external"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(5);
                subtitle.Language.ShouldBe("en");
            }
        }

        [Test]
        public async Task Should_Select_Condition_Audio_Title()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                    audio_condition: "title like '%movie%'"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(2);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_Condition_Audio_Channels()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "en*"
                    audio_condition: "channels > 2"
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(2);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_Prioritized_Audio_Language()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["en*","ja"]
                    audio_title_blocklist: ["riff"]
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeTrue();

            foreach (MediaStream audioStream in result.AudioStream)
            {
                audioStream.Index.ShouldBe(2);
                audioStream.Language.ShouldBe("eng");
            }
        }

        [Test]
        public async Task Should_Select_Prioritized_Subtitle_Language()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language:
                    - "*"
                    subtitle_language: ["jp","en*"]
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.Subtitle.IsSome.ShouldBeTrue();

            foreach (Subtitle subtitle in result.Subtitle)
            {
                subtitle.Id.ShouldBe(6);
                subtitle.Language.ShouldBe("jp");
            }
        }

        [Test]
        public async Task Should_Select_No_Streams_When_Languages_Do_Not_Match()
        {
            const string YAML =
                """
                ---
                items:
                  - audio_language: ["en"]
                    subtitle_language: ["es*","de*"]
                  - audio_language: ["ja"]
                    subtitle_language: ["es*","de*"]
                """;

            var fileSystem = new MockFileSystem();
            fileSystem.Initialize()
                .WithFile(TestFileName).Which(f => f.HasStringContent(YAML));
            var streamSelector = new CustomStreamSelector(fileSystem, _logger);

            StreamSelectorResult result = await streamSelector.SelectStreams(
                _channel,
                DateTimeOffset.Now,
                _audioVersion,
                _subtitles,
                shouldLogMessages: true);

            result.AudioStream.IsSome.ShouldBeFalse();
            result.Subtitle.IsSome.ShouldBeFalse();
        }

        private static MediaItemAudioVersion GetTestAudioVersion(string englishLanguage)
        {
            var mediaItem = new OtherVideo();
            var mediaVersion = new MediaVersion
            {
                Streams =
                [
                    new MediaStream
                    {
                        Index = 0,
                        MediaStreamKind = MediaStreamKind.Audio,
                        Channels = 2,
                        Language = "ja",
                        Title = "Some Title"
                    },
                    new MediaStream
                    {
                        Index = 1,
                        MediaStreamKind = MediaStreamKind.Audio,
                        Channels = 2,
                        Language = englishLanguage,
                        Title = "Riff Title",
                        Default = true
                    },
                    new MediaStream
                    {
                        Index = 2,
                        MediaStreamKind = MediaStreamKind.Audio,
                        Channels = 6,
                        Language = englishLanguage,
                        Title = "Movie Title"
                    },
                    new MediaStream
                    {
                        Index = 3,
                        MediaStreamKind = MediaStreamKind.Audio,
                        Channels = 2,
                        Title = "Who Knows"
                    }
                ]
            };

            return new MediaItemAudioVersion(mediaItem, mediaVersion);
        }
    }
}
