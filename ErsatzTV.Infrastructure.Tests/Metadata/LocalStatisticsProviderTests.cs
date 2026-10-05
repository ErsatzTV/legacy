using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.Infrastructure.Metadata;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Infrastructure.Tests.Metadata;

[TestFixture]
public class LocalStatisticsProviderTests
{
    [Test]
    // this needs to be a culture where '.' is a group separator
    [SetCulture("it-IT")]
    public void Test()
    {
        var provider = new LocalStatisticsProvider(
            Substitute.For<IMetadataRepository>(),
            new MockFileSystem(),
            Substitute.For<ILocalFileSystem>(),
            Substitute.For<IHardwareCapabilitiesFactory>(),
            Substitute.For<ILogger<LocalStatisticsProvider>>());

        var input = new LocalStatisticsProvider.FFprobe(
            new LocalStatisticsProvider.FFprobeFormat(
                "123.45",
                new LocalStatisticsProvider.FFprobeTags(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty)),
            [],
            [],
            []);

        MediaVersion result = provider.ProjectToMediaVersion("test", input);

        result.Duration.ShouldBe(TimeSpan.FromSeconds(123.45));
    }

    // subtitle runs past the video, so format duration is too long
    private const string MatroskaLongSubtitle = """
        {
          "format": { "duration": "1249.413000" },
          "streams": [
            { "index": 0, "codec_type": "video", "tags": { "DURATION": "00:18:39.164000000" } },
            { "index": 1, "codec_type": "audio", "tags": { "language": "eng", "DURATION": "00:18:40.132000000" } },
            { "index": 2, "codec_type": "subtitle", "duration": "1249.413000", "tags": { "DURATION": "00:20:49.413000000" } }
          ]
        }
        """;

    private const string MatroskaLanguageTag = """
        {
          "format": { "duration": "1249.413000" },
          "streams": [
            { "index": 0, "codec_type": "video", "tags": { "DURATION-eng": "25:00:01.500000000" } },
            { "index": 1, "codec_type": "subtitle", "duration": "1249.413000" }
          ]
        }
        """;

    private const string StreamDuration = """
        {
          "format": { "duration": "65.000000" },
          "streams": [
            { "index": 0, "codec_type": "video", "duration": "60.060000" },
            { "index": 1, "codec_type": "audio", "duration": "60.100000" }
          ]
        }
        """;

    private const string SongWithCoverArt = """
        {
          "format": { "duration": "200.000000" },
          "streams": [
            { "index": 0, "codec_type": "audio", "duration": "180.500000" },
            { "index": 1, "codec_type": "video", "disposition": { "attached_pic": 1 }, "duration": "0.000000" }
          ]
        }
        """;

    private const string VideoWithoutDuration = """
        {
          "format": { "duration": "100.000000" },
          "streams": [
            { "index": 0, "codec_type": "video" },
            { "index": 1, "codec_type": "audio", "duration": "90.000000" }
          ]
        }
        """;

    private const string PartialAudioDurations = """
        {
          "format": { "duration": "100.000000" },
          "streams": [
            { "index": 0, "codec_type": "audio", "duration": "90.000000" },
            { "index": 1, "codec_type": "audio" }
          ]
        }
        """;

    [TestCase(MatroskaLongSubtitle, 1119.164)]
    [TestCase(MatroskaLanguageTag, 90001.5)]
    [TestCase(StreamDuration, 60.06)]
    [TestCase(SongWithCoverArt, 180.5)]
    [TestCase(VideoWithoutDuration, 100.0)]
    [TestCase(PartialAudioDurations, 100.0)]
    public void Duration_Should_Match_Content(string json, double expectedSeconds)
    {
        var provider = new LocalStatisticsProvider(
            Substitute.For<IMetadataRepository>(),
            new MockFileSystem(),
            Substitute.For<ILocalFileSystem>(),
            Substitute.For<IHardwareCapabilitiesFactory>(),
            Substitute.For<ILogger<LocalStatisticsProvider>>());

        var input = JsonConvert.DeserializeObject<LocalStatisticsProvider.FFprobe>(json);

        MediaVersion result = provider.ProjectToMediaVersion("test", input);

        result.Duration.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }
}
