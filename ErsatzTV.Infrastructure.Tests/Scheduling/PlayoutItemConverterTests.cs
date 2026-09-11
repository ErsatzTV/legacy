using System.Collections.Immutable;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Security;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Scheduling;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;
using static LanguageExt.Prelude;
using Next = ErsatzTV.Core.Next;

namespace ErsatzTV.Infrastructure.Tests.Scheduling;

[TestFixture]
public class PlayoutItemConverterTests
{
    private const string VideoPath = "/media/movie.mkv";
    private MockFileSystem _fileSystem = null!;
    private PlayoutItemConverter _converter = null!;
    private Channel _channel = null!;
    private DateTimeOffset _start;

    [SetUp]
    public void SetUp()
    {
        _start = DateTimeOffset.UtcNow.AddDays(1);
        _fileSystem = new MockFileSystem();
        _fileSystem.Directory.CreateDirectory("/media");
        _fileSystem.File.WriteAllText(VideoPath, string.Empty);
        var plex = Substitute.For<IPlexPathReplacementService>();
        plex.GetReplacementPlexPath(default, default!, default, default).ReturnsForAnyArgs(VideoPath);
        var jellyfin = Substitute.For<IJellyfinPathReplacementService>();
        jellyfin.GetReplacementJellyfinPath(default, default!, default, default).ReturnsForAnyArgs(VideoPath);
        var emby = Substitute.For<IEmbyPathReplacementService>();
        emby.GetReplacementEmbyPath(default, default!, default, default).ReturnsForAnyArgs(VideoPath);

        var selector = Substitute.For<IFFmpegStreamSelector>();
        selector.SelectSubtitleStream(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(
                call.Arg<ImmutableList<Subtitle>>().HeadOrNone()));
        var watermarks = Substitute.For<IWatermarkSelector>();
        watermarks.SelectWatermarks(default, default!, default!, default, default).ReturnsForAnyArgs([]);
        var graphics = Substitute.For<IGraphicsElementSelector>();
        graphics.SelectGraphicsElements(default!, default!, default, default).ReturnsForAnyArgs([]);
        _converter = new PlayoutItemConverter(
            _fileSystem, plex, jellyfin, emby,
            Substitute.For<ICustomStreamSelector>(), selector, watermarks, graphics,
            Substitute.For<IDbContextFactory<TvContext>>(),
            Substitute.For<ILogger<PlayoutItemConverter>>());
        _channel = new Channel(Guid.NewGuid()) { StreamSelectorMode = ChannelStreamSelectorMode.Default };
    }

    [TestCase("jellyfin", null)]
    [TestCase("plex", "/library/streams/123")]
    [TestCase("emby", "media-source-id")]
    [TestCase("jellyfin-episode", null)]
    [TestCase("plex-episode", "/library/streams/123")]
    [TestCase("emby-episode", "media-source-id")]
    public async Task Server_sidecars_use_signed_subtitle_proxy(string server, string? path)
    {
        MediaItem movie = server switch
        {
            "jellyfin" => new JellyfinMovie(),
            "plex" => new PlexMovie(),
            "jellyfin-episode" => new JellyfinEpisode(),
            "plex-episode" => new PlexEpisode(),
            "emby-episode" => new EmbyEpisode(),
            _ => new EmbyMovie()
        };
        var subtitle = new Subtitle
        {
            Id = 9873, SubtitleKind = SubtitleKind.Sidecar, Codec = "subrip",
            StreamIndex = 100000, Path = path!
        };

        Next.PlayoutItem result = await Convert(movie, [subtitle]);
        Next.Source source = result.Tracks!.Subtitle!.Source!;
        source.SourceType.ShouldBe(Next.SourceType.Http);
        source.Path.ShouldBeNull();
        var uri = new Uri(source.Uri!);
        uri.AbsolutePath.ShouldBe("/internal/media/subtitle/9873");
        var query = uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
        InternalUrlSigner.Verify(query["exp"], query["sig"], "subtitle", "9873").ShouldBeTrue();
        long.Parse(query["exp"]).ShouldBe(_start.AddSeconds(30).AddHours(2).ToUnixTimeSeconds());
        query.ContainsKey("seekToMs").ShouldBeFalse();
        AssertTiming(source);
        subtitle.Path.ShouldBe(path);

        using JsonDocument json = JsonDocument.Parse(Next.Serialize.ToJson(new Next.Playout
        {
            Version = "https://ersatztv.org/playout/version/0.0.4", Items = [result]
        }));
        JsonElement serialized = json.RootElement.GetProperty("items")[0]
            .GetProperty("tracks").GetProperty("subtitle").GetProperty("source");
        serialized.GetProperty("source_type").GetString().ShouldBe("http");
        serialized.GetProperty("uri").GetString().ShouldBe(source.Uri);
    }

    [Test]
    public async Task Persisted_local_sidecar_uses_proxy_after_checking_file_exists()
    {
        _fileSystem.File.WriteAllText("/media/movie.srt", string.Empty);
        Next.PlayoutItem result = await Convert(new Movie(),
            [new Subtitle { Id = 123, SubtitleKind = SubtitleKind.Sidecar, Path = "/media/movie.srt" }]);
        Next.Source source = result.Tracks!.Subtitle!.Source!;
        source.SourceType.ShouldBe(Next.SourceType.Http);
        new Uri(source.Uri!).AbsolutePath.ShouldBe("/internal/media/subtitle/123");
        AssertTiming(source);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("/media/missing.srt")]
    public async Task Unavailable_local_sidecars_do_not_emit_subtitle_tracks(string? path)
    {
        var subtitle = new Subtitle { Id = 123, SubtitleKind = SubtitleKind.Sidecar, Path = path! };
        Next.PlayoutItem result = await Convert(new Movie(), [subtitle]);
        result.Tracks?.Subtitle.ShouldBeNull();
    }

    [TestCase("jellyfin", 0, null)]
    [TestCase("plex", 123, null)]
    [TestCase("emby", 123, "")]
    public async Task Unresolvable_server_sidecars_do_not_emit_subtitle_tracks(string server, int id, string? path)
    {
        Movie movie = server switch
        {
            "jellyfin" => new JellyfinMovie(),
            "plex" => new PlexMovie(),
            _ => new EmbyMovie()
        };
        Next.PlayoutItem result = await Convert(movie,
            [new Subtitle { Id = id, SubtitleKind = SubtitleKind.Sidecar, Path = path! }]);
        result.Tracks?.Subtitle.ShouldBeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Extracted_subtitle_requires_existing_cache_file(bool exists)
    {
        string path = Path.Combine(FileSystemLayout.SubtitleCacheFolder, "movie.srt");
        if (exists)
        {
            _fileSystem.Directory.CreateDirectory(FileSystemLayout.SubtitleCacheFolder);
            _fileSystem.File.WriteAllText(path, string.Empty);
        }

        Next.PlayoutItem result = await Convert(new Movie(),
            [new Subtitle { SubtitleKind = SubtitleKind.Embedded, IsExtracted = true, Path = "movie.srt" }]);
        if (exists)
        {
            Next.Source source = result.Tracks!.Subtitle!.Source!;
            source.Path.ShouldBe(path);
            source.SourceType.ShouldBe(Next.SourceType.Local);
            AssertTiming(source);
        }
        else
        {
            result.Tracks?.Subtitle.ShouldBeNull();
        }
    }

    [TestCase("http://localhost:8409/subtitle.srt")]
    [TestCase("http://127.0.0.1:8409/subtitle.srt")]
    [TestCase("https://example.com/subtitle.srt")]
    public async Task Remote_subtitles_preserve_uri_and_media_timing(string uri)
    {
        Next.PlayoutItem result = await Convert(new Movie(),
            [new Subtitle { SubtitleKind = SubtitleKind.Sidecar, Path = uri }]);
        Next.Source source = result.Tracks!.Subtitle!.Source!;
        source.SourceType.ShouldBe(Next.SourceType.Http);
        source.Uri.ShouldBe(uri);
        AssertTiming(source);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Embedded_image_subtitle_has_effective_video_source(bool hasAudio)
    {
        Next.PlayoutItem result = await Convert(new Movie(),
            [new Subtitle { SubtitleKind = SubtitleKind.Embedded, Codec = "hdmv_pgs_subtitle", StreamIndex = 4 }],
            hasAudio);
        Next.TrackSelection track = result.Tracks!.Subtitle!;
        track.StreamIndex.ShouldBe(4);
        Next.Source source = (track.Source ?? result.Source)!;
        source.ShouldNotBeNull();
        source.Path.ShouldBe(VideoPath);
        AssertTiming(source);
        if (!hasAudio)
        {
            result.Source.ShouldBeNull();
            track.Source.ShouldBeSameAs(result.Tracks.Video!.Source);
        }
    }

    [Test]
    public async Task Music_video_uses_metadata_subtitles_when_credits_are_disabled()
    {
        _fileSystem.File.WriteAllText("/media/music.srt", string.Empty);
        Next.PlayoutItem result = await Convert(new MusicVideo(),
            [new Subtitle { SubtitleKind = SubtitleKind.Sidecar, Path = "/media/music.srt" }]);
        result.Tracks!.Subtitle!.Source!.Path.ShouldBe("/media/music.srt");
    }

    [Test]
    public async Task Generated_credits_do_not_apply_in_point_twice()
    {
        _channel.MusicVideoCreditsMode = ChannelMusicVideoCreditsMode.GenerateSubtitles;
        Next.PlayoutItem result = await Convert(new MusicVideo(), []);
        Next.Source source = result.Tracks!.Subtitle!.Source!;
        source.SourceType.ShouldBe(Next.SourceType.Http);
        source.Uri.ShouldEndWith("/internal/ffmpeg/music-video-credits/42?seekToMs=10000");
        source.InPointMs.ShouldBeNull();
        source.OutPointMs.ShouldBeNull();
    }

    [Test]
    public async Task Filtering_does_not_mutate_metadata_or_supplied_subtitles()
    {
        List<Subtitle> subtitles =
        [
            new() { SubtitleKind = SubtitleKind.Embedded, Codec = "eia_608" },
            new() { SubtitleKind = SubtitleKind.Sidecar, Codec = "dvd_subtitle" }
        ];
        await Convert(new JellyfinMovie(), subtitles);
        subtitles.Count.ShouldBe(2);
        await Convert(new JellyfinMovie(), subtitles, suppliedSubtitles: Some(subtitles));
        subtitles.Count.ShouldBe(2);
    }

    private async Task<Next.PlayoutItem> Convert(
        MediaItem mediaItem,
        List<Subtitle> subtitles,
        bool hasAudio = true,
        Option<List<Subtitle>> suppliedSubtitles = default)
    {
        var version = new MediaVersion
        {
            Duration = TimeSpan.FromMinutes(2),
            MediaFiles = [new MediaFile { Path = VideoPath }],
            Streams = [new MediaStream { MediaStreamKind = MediaStreamKind.Video, Index = 0 }]
        };
        if (hasAudio)
        {
            version.Streams.Add(new MediaStream { MediaStreamKind = MediaStreamKind.Audio, Index = 1 });
        }

        switch (mediaItem)
        {
            case Episode episode:
                episode.MediaVersions = [version];
                episode.EpisodeMetadata = [new EpisodeMetadata { Subtitles = subtitles }];
                break;
            case Movie movie:
                movie.MediaVersions = [version];
                movie.MovieMetadata = [new MovieMetadata { Subtitles = subtitles }];
                break;
            case MusicVideo musicVideo:
                musicVideo.MediaVersions = [version];
                musicVideo.MusicVideoMetadata = [new MusicVideoMetadata { Subtitles = subtitles }];
                break;
        }

        var playoutItem = new PlayoutItem
        {
            Id = 42,
            MediaItem = mediaItem,
            Start = _start.UtcDateTime,
            Finish = _start.AddSeconds(30).UtcDateTime,
            InPoint = TimeSpan.FromSeconds(10),
            OutPoint = TimeSpan.FromSeconds(40)
        };
        Option<Next.PlayoutItem> result = await _converter.ToNext(
            Some(_channel), None, TimeSpan.Zero, playoutItem, suppliedSubtitles, true, CancellationToken.None);
        result.IsSome.ShouldBeTrue();
        return result.Match(item => item, () => throw new InvalidOperationException("No playout item"));
    }

    private static void AssertTiming(Next.Source source)
    {
        source.InPointMs.ShouldBe(10000);
        source.OutPointMs.ShouldBe(40000);
    }
}
