using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Scripting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class FFmpegStreamSelectorTests
{
    [TestFixture]
    public class SelectAudioStream
    {
        [Test]
        [CancelAfter(1000)]
        public async Task Should_Select_Audio_Stream_With_Preferred_Language(CancellationToken cancellationToken)
        {
            // skip movie/episode script paths by using other video
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
                        Channels = 6,
                        Language = "eng",
                        Title = "Another Title",
                        Default = true
                    }
                ]
            };

            var audioVersion = new MediaItemAudioVersion(mediaItem, mediaVersion);
            var channel = new Channel(Guid.NewGuid())
            {
                PreferredAudioLanguageCode = "eng"
            };

            ILanguageCodeService languageCodeService = Substitute.For<ILanguageCodeService>();
            languageCodeService.GetAllLanguageCodes(Arg.Any<List<string>>())
                .Returns(["jpn"]);

            var selector = new FFmpegStreamSelector(
                new ScriptEngine(Substitute.For<ILogger<ScriptEngine>>()),
                Substitute.For<IStreamSelectorRepository>(),
                Substitute.For<IConfigElementRepository>(),
                new MockFileSystem(),
                languageCodeService,
                Substitute.For<ILogger<FFmpegStreamSelector>>());

            Option<MediaStream> selectedStream = await selector.SelectAudioStream(
                audioVersion,
                StreamingMode.TransportStream,
                channel,
                "jpn",
                "Whatever",
                shouldLogMessages: false,
                cancellationToken);
            selectedStream.IsSome.ShouldBeTrue();
            foreach (MediaStream stream in selectedStream)
            {
                stream.Language.ShouldBe("ja");
            }
        }

        [Test]
        [CancelAfter(1000)]
        public async Task Should_Select_Audio_Stream_With_Preferred_Title(CancellationToken cancellationToken)
        {
            // skip movie/episode script paths by using other video
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
                        Channels = 6,
                        Language = "eng",
                        Title = "Another Title",
                        Default = true
                    }
                ]
            };

            var audioVersion = new MediaItemAudioVersion(mediaItem, mediaVersion);
            var channel = new Channel(Guid.NewGuid())
            {
                PreferredAudioTitle = "Some"
            };

            ILanguageCodeService languageCodeService = Substitute.For<ILanguageCodeService>();
            languageCodeService.GetAllLanguageCodes(Arg.Any<List<string>>())
                .Returns(["jpn", "eng"]);

            var selector = new FFmpegStreamSelector(
                new ScriptEngine(Substitute.For<ILogger<ScriptEngine>>()),
                Substitute.For<IStreamSelectorRepository>(),
                Substitute.For<IConfigElementRepository>(),
                new MockFileSystem(),
                languageCodeService,
                Substitute.For<ILogger<FFmpegStreamSelector>>());

            Option<MediaStream> selectedStream = await selector.SelectAudioStream(
                audioVersion,
                StreamingMode.TransportStream,
                channel,
                null,
                channel.PreferredAudioTitle,
                shouldLogMessages: false,
                cancellationToken);
            selectedStream.IsSome.ShouldBeTrue();
            foreach (MediaStream stream in selectedStream)
            {
                stream.Language.ShouldBe("ja");
            }
        }

        [Test]
        [CancelAfter(1000)]
        public async Task Should_Select_Subtitle_Stream_With_Preferred_Language(CancellationToken cancellationToken)
        {
            // skip movie/episode script paths by using other video
            var subtitles = new List<Subtitle>
            {
                new()
                {
                    StreamIndex = 0,
                    SubtitleKind = SubtitleKind.Sidecar,
                    Language = "eng",
                    Default = true
                },
                new()
                {
                    StreamIndex = 1,
                    SubtitleKind = SubtitleKind.Sidecar,
                    Language = "he"
                }
            };

            var channel = new Channel(Guid.NewGuid());

            ILanguageCodeService languageCodeService = Substitute.For<ILanguageCodeService>();
            languageCodeService.GetAllLanguageCodes(Arg.Any<List<string>>())
                .Returns(["heb"]);

            var selector = new FFmpegStreamSelector(
                new ScriptEngine(Substitute.For<ILogger<ScriptEngine>>()),
                Substitute.For<IStreamSelectorRepository>(),
                Substitute.For<IConfigElementRepository>(),
                new MockFileSystem(),
                languageCodeService,
                Substitute.For<ILogger<FFmpegStreamSelector>>());

            Option<Subtitle> selectedStream = await selector.SelectSubtitleStream(
                subtitles.ToImmutableList(),
                channel,
                "heb",
                ChannelSubtitleMode.Any,
                shouldLogMessages: true,
                cancellationToken);
            selectedStream.IsSome.ShouldBeTrue();
            foreach (Subtitle stream in selectedStream)
            {
                stream.Language.ShouldBe("he");
            }
        }
    }

    [TestFixture]
    public class SelectSubtitleStream
    {
        [Test]
        [CancelAfter(1000)]
        public async Task Should_Skip_Unextracted_Embedded_Text_Subtitle(
            [Values] StreamingEngine streamingEngine,
            CancellationToken cancellationToken)
        {
            var subtitles = new List<Subtitle>
            {
                EmbeddedText(isExtracted: false, path: null),
                Sidecar()
            };

            Option<Subtitle> selected = await Select(
                subtitles,
                streamingEngine,
                useEmbeddedSubtitles: true,
                cancellationToken);

            selected.Map(s => s.SubtitleKind).ShouldBe(Some(SubtitleKind.Sidecar));
        }

        [Test]
        [CancelAfter(1000)]
        public async Task Should_Select_Extracted_Embedded_Text_Subtitle(
            [Values] StreamingEngine streamingEngine,
            CancellationToken cancellationToken)
        {
            var subtitles = new List<Subtitle>
            {
                EmbeddedText(isExtracted: true, path: "ron.srt"),
                Sidecar()
            };

            Option<Subtitle> selected = await Select(
                subtitles,
                streamingEngine,
                useEmbeddedSubtitles: true,
                cancellationToken);

            selected.Map(s => s.SubtitleKind).ShouldBe(Some(SubtitleKind.Embedded));
        }

        [Test]
        [CancelAfter(1000)]
        public async Task Should_Select_Unextracted_Embedded_Image_Subtitle(
            [Values] StreamingEngine streamingEngine,
            CancellationToken cancellationToken)
        {
            var subtitles = new List<Subtitle> { EmbeddedImage(), Sidecar() };

            Option<Subtitle> selected = await Select(
                subtitles,
                streamingEngine,
                useEmbeddedSubtitles: true,
                cancellationToken);

            selected.Map(s => s.SubtitleKind).ShouldBe(Some(SubtitleKind.Embedded));
        }

        [Test]
        [CancelAfter(1000)]
        public async Task Should_Ignore_Embedded_Subtitles_When_Disabled(
            [Values] StreamingEngine streamingEngine,
            CancellationToken cancellationToken)
        {
            var subtitles = new List<Subtitle>
            {
                EmbeddedImage(),
                EmbeddedText(isExtracted: true, path: "ron.srt"),
                Sidecar()
            };

            Option<Subtitle> selected = await Select(
                subtitles,
                streamingEngine,
                useEmbeddedSubtitles: false,
                cancellationToken);

            selected.Map(s => s.SubtitleKind).ShouldBe(Some(SubtitleKind.Sidecar));
        }

        private static Subtitle EmbeddedText(bool isExtracted, string path) => new()
        {
            StreamIndex = 2,
            SubtitleKind = SubtitleKind.Embedded,
            Codec = "mov_text",
            Language = "ron",
            Default = true,
            IsExtracted = isExtracted,
            Path = path
        };

        private static Subtitle EmbeddedImage() => new()
        {
            StreamIndex = 3,
            SubtitleKind = SubtitleKind.Embedded,
            Codec = "hdmv_pgs_subtitle",
            Language = "ron",
            Default = true
        };

        private static Subtitle Sidecar() => new()
        {
            StreamIndex = 100001,
            SubtitleKind = SubtitleKind.Sidecar,
            Codec = "subrip",
            Language = "ron"
        };

        private static async Task<Option<Subtitle>> Select(
            List<Subtitle> subtitles,
            StreamingEngine streamingEngine,
            bool useEmbeddedSubtitles,
            CancellationToken cancellationToken)
        {
            var channel = new Channel(Guid.NewGuid())
            {
                StreamingEngine = streamingEngine,
                StreamingMode = StreamingMode.TransportStreamHybrid
            };

            IConfigElementRepository configElementRepository = Substitute.For<IConfigElementRepository>();
            configElementRepository
                .GetValue<bool>(
                    Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.FFmpegUseEmbeddedSubtitles.Key),
                    Arg.Any<CancellationToken>())
                .Returns(Some(useEmbeddedSubtitles));

            ILanguageCodeService languageCodeService = Substitute.For<ILanguageCodeService>();
            languageCodeService.GetAllLanguageCodes(Arg.Any<List<string>>())
                .Returns(["ron", "rum", "ro"]);

            var selector = new FFmpegStreamSelector(
                new ScriptEngine(Substitute.For<ILogger<ScriptEngine>>()),
                Substitute.For<IStreamSelectorRepository>(),
                configElementRepository,
                new MockFileSystem(),
                languageCodeService,
                Substitute.For<ILogger<FFmpegStreamSelector>>());

            return await selector.SelectSubtitleStream(
                subtitles.ToImmutableList(),
                channel,
                "ron",
                ChannelSubtitleMode.Default,
                shouldLogMessages: false,
                cancellationToken);
        }
    }
}
