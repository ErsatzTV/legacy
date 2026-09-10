using System.Collections.Immutable;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Security;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using MediaStream = ErsatzTV.Core.Domain.MediaStream;
using PlayoutItem = ErsatzTV.Core.Domain.PlayoutItem;

namespace ErsatzTV.Infrastructure.Scheduling;

public class PlayoutItemConverter(
    IFileSystem fileSystem,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    ICustomStreamSelector customStreamSelector,
    IFFmpegStreamSelector ffmpegStreamSelector,
    IWatermarkSelector watermarkSelector,
    IGraphicsElementSelector graphicsElementSelector,
    IDbContextFactory<TvContext> dbContextFactory) : IPlayoutItemConverter
{
    public async Task<Option<Core.Next.PlayoutItem>> ToNext(
        string channelNumber,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        TimeSpan playoutOffset = TimeSpan.Zero;

        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.MirrorSourceChannel)
            .Filter(c => c.PlayoutSource == ChannelPlayoutSource.Mirror && c.MirrorSourceChannelId != null)
            .SelectOneAsync(
                c => c.Number == channelNumber,
                c => c.Number == channelNumber,
                cancellationToken);
        foreach (Channel channel in maybeChannel)
        {
            playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;
        }

        Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
            .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
            .BindT(watermarkId => dbContext.ChannelWatermarks
                .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

        Option<Channel> maybeChannelForArtwork = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.Watermark)
            .Include(c => c.Artwork)
            .SingleOrDefaultAsync(c => c.Number == channelNumber, cancellationToken)
            .Map(Optional);

        return await ToNext(
            maybeChannelForArtwork,
            maybeGlobalWatermark,
            playoutOffset,
            playoutItem,
            Option<List<Subtitle>>.None,
            false,
            cancellationToken);
    }

    public async Task<Option<Core.Next.PlayoutItem>> ToNext(
        Option<Channel> maybeChannel,
        Option<ChannelWatermark> maybeGlobalWatermark,
        TimeSpan playoutOffset,
        PlayoutItem playoutItem,
        Option<List<Subtitle>> subtitles,
        bool shouldLogMessages,
        CancellationToken cancellationToken)
    {
        if (playoutItem is not DynamicPlayoutItem &&
            playoutItem.MediaItem is not Episode && playoutItem.MediaItem is not Movie &&
            playoutItem.MediaItem is not OtherVideo && playoutItem.MediaItem is not MusicVideo &&
            playoutItem.MediaItem is not RemoteStream && playoutItem.MediaItem is not Image)
        {
            return Option<Core.Next.PlayoutItem>.None;
        }

        playoutItem.Start += playoutOffset;
        playoutItem.Finish += playoutOffset;

        var nextPlayoutItem = new Core.Next.PlayoutItem
        {
            Id = playoutItem is DynamicPlayoutItem
                ? Guid.NewGuid().ToString()
                : playoutItem.Id.ToString(CultureInfo.InvariantCulture),
            Start = playoutItem.StartOffset,
            Finish = playoutItem.FinishOffset
        };

        Option<Core.Next.Source> maybeSource = await SourceForItem(playoutItem, cancellationToken);
        if (maybeSource.IsNone)
        {
            return Option<Core.Next.PlayoutItem>.None;
        }

        foreach (Core.Next.Source source in maybeSource)
        {
            SetInOutPoints(playoutItem, source);
            nextPlayoutItem.Source = source;
        }

        if (playoutItem is not DynamicPlayoutItem)
        {
            MediaVersion headVersion = playoutItem.MediaItem.GetHeadVersion();
            var sourceVideoHints = headVersion.Streams
                .Where(s => s.MediaStreamKind is MediaStreamKind.Video)
                .Select(s => new Core.Next.VideoHint
                {
                    StreamIndex = s.Index,
                    Codec = s.Codec,
                    Height = headVersion.Height,
                    Width = headVersion.Width,
                    Profile = s.Profile,
                    FieldOrder = headVersion.VideoScanKind is VideoScanKind.Interlaced ? "tt" : "progressive",
                    PixFmt = string.IsNullOrWhiteSpace(s.PixelFormat)
                        ? PixelFormatForBitDepth(s.BitsPerRawSample)
                        : s.PixelFormat,
                    FrameRate = headVersion.RFrameRate,
                    SampleAspectRatio = headVersion.SampleAspectRatio,
                    DisplayAspectRatio = headVersion.DisplayAspectRatio,
                    ColorPrimaries = s.ColorPrimaries,
                    ColorRange = s.ColorRange,
                    ColorSpace = s.ColorSpace,
                    ColorTransfer = s.ColorTransfer,
                    DvProfile = s.DvProfile,
                    HasHdr10Metadata = s.HasHdr10Metadata,
                }).ToList();
            var sourceAudioHints = headVersion.Streams
                .Where(s => s.MediaStreamKind is MediaStreamKind.Audio)
                .Select(s => new Core.Next.AudioHint
                {
                    StreamIndex = s.Index,
                    Codec = s.Codec,
                    Channels = s.Channels
                }).ToList();
            var sourceSubtitleHints = headVersion.Streams
                .Where(s => s.MediaStreamKind is MediaStreamKind.Subtitle)
                .Select(s => new Core.Next.SubtitleHint
                {
                    StreamIndex = s.Index,
                    Codec = s.Codec
                }).ToList();

            nextPlayoutItem.Source!.ProbeHint = new Core.Next.ProbeHint
            {
                Audio = sourceAudioHints,
                Video = sourceVideoHints,
                Subtitle = sourceSubtitleHints,
                DurationMs = (long)headVersion.Duration.TotalMilliseconds
            };

            // if no audio streams, use lavfi to insert silence
            if (headVersion.Streams.All(s => s.MediaStreamKind is not MediaStreamKind.Audio))
            {
                var videoSource = nextPlayoutItem.Source;

                nextPlayoutItem.Source = null;
                nextPlayoutItem.Tracks = new Core.Next.PlayoutItemTracks
                {
                    Audio = new Core.Next.TrackSelection
                    {
                        Source =
                            new Core.Next.Source
                            {
                                SourceType = Core.Next.SourceType.Lavfi,
                                Params = "anullsrc=channel_layout=stereo:sample_rate=48000",
                                ProbeHint = new Core.Next.ProbeHint
                                {
                                    Audio =
                                    [
                                        new Core.Next.AudioHint
                                        {
                                            StreamIndex = 0,
                                            Codec = "pcm_s16le",
                                            Channels = 2
                                        }
                                    ]
                                }
                            }
                    },
                    Video = new Core.Next.TrackSelection
                    {
                        Source = videoSource
                    }
                };
            }

            foreach (Channel channel in maybeChannel)
            {
                var audioVersion = new MediaItemAudioVersion(playoutItem.MediaItem, headVersion);
                await SelectTracks(
                    channel,
                    playoutItem,
                    audioVersion,
                    nextPlayoutItem,
                    playoutItem.PreferredAudioLanguageCode ?? channel.PreferredAudioLanguageCode,
                    playoutItem.PreferredAudioTitle ?? channel.PreferredAudioTitle,
                    playoutItem.PreferredSubtitleLanguageCode ?? channel.PreferredSubtitleLanguageCode,
                    playoutItem.SubtitleMode ?? channel.SubtitleMode,
                    subtitles,
                    shouldLogMessages,
                    cancellationToken);
                SelectGraphics(
                    maybeGlobalWatermark,
                    channel,
                    playoutItem,
                    nextPlayoutItem,
                    headVersion.RFrameRate,
                    shouldLogMessages);
            }
        }

        return nextPlayoutItem;
    }

    private static string PixelFormatForBitDepth(int bitDepth)
    {
        return bitDepth switch
        {
            10 => "yuv420p10le",
            _ => "yuv420p"
        };
    }

    private async Task<Option<Core.Next.Source>> SourceForItem(
        PlayoutItem playoutItem,
        CancellationToken cancellationToken)
    {
        DateTimeOffset exp = playoutItem.FinishOffset + TimeSpan.FromHours(2);

        if (playoutItem is DynamicPlayoutItem)
        {
            string sig = InternalUrlSigner.Sign(exp, "fallback");

            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Dynamic,
                Uri =
                    $"http://localhost:{Settings.StreamingPort}/internal/media/fallback?exp={exp.ToUnixTimeSeconds()}&sig={sig}"
            };
        }

        if (playoutItem.MediaItem is RemoteStream remoteStream)
        {
            if (!string.IsNullOrWhiteSpace(remoteStream.Url))
            {
                if (remoteStream.Url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                {
                    return new Core.Next.Source
                    {
                        SourceType = Core.Next.SourceType.Rtsp,
                        Uri = remoteStream.Url
                    };
                }

                return new Core.Next.Source
                {
                    SourceType = Core.Next.SourceType.Http,
                    Uri = remoteStream.Url,
                    IsLive = remoteStream.IsLive,
                    KeepAlive = true,
                    Reconnect = true
                };
            }

            if (!string.IsNullOrWhiteSpace(remoteStream.Script))
            {
                var split = CommandLineParser.SplitCommandLine(remoteStream.Script).ToList();
                if (split.Count > 0)
                {
                    var source = new Core.Next.Source
                    {
                        SourceType = Core.Next.SourceType.Script,
                        Command = split.Head(),
                        IsLive = remoteStream.IsLive
                    };

                    if (split.Count > 1)
                    {
                        source.Args = split.Tail().ToList();
                    }

                    return source;
                }
            }

            return Option<Core.Next.Source>.None;
        }

        string path = await playoutItem.MediaItem.GetLocalPath(
            plexPathReplacementService,
            jellyfinPathReplacementService,
            embyPathReplacementService,
            cancellationToken,
            log: false);

        // check filesystem first
        if (fileSystem.File.Exists(path))
        {
            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Local,
                Path = path,
            };
        }

        MediaFile file = playoutItem.MediaItem.GetHeadVersion().MediaFiles.Head();
        int mediaSourceId = playoutItem.MediaItem.LibraryPath.Library.MediaSourceId;
        if (file is PlexMediaFile pmf)
        {
            string sig = InternalUrlSigner.Sign(
                exp,
                "plex",
                $"{mediaSourceId}",
                $"{pmf.Key}");

            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri =
                    $"http://localhost:{Settings.StreamingPort}/internal/media/plex/{mediaSourceId}/{pmf.Key}?exp={exp.ToUnixTimeSeconds()}&sig={sig}",
                KeepAlive = false,
                Reconnect = true
            };
        }

        Option<string> jellyfinItemId = playoutItem.MediaItem switch
        {
            JellyfinEpisode e => e.ItemId,
            JellyfinMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in jellyfinItemId)
        {
            string sig = InternalUrlSigner.Sign(exp, "jellyfin", $"{itemId}");

            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri =
                    $"http://localhost:{Settings.StreamingPort}/internal/media/jellyfin/{itemId}?exp={exp.ToUnixTimeSeconds()}&sig={sig}",
                KeepAlive = false,
                Reconnect = true
            };
        }

        // attempt to remotely stream emby
        Option<string> embyItemId = playoutItem.MediaItem switch
        {
            EmbyEpisode e => e.ItemId,
            EmbyMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in embyItemId)
        {
            string sig = InternalUrlSigner.Sign(exp, "emby", $"{itemId}");

            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri =
                    $"http://localhost:{Settings.StreamingPort}/internal/media/emby/{itemId}?exp={exp.ToUnixTimeSeconds()}&sig={sig}",
                KeepAlive = false,
                Reconnect = true
            };
        }

        return Option<Core.Next.Source>.None;
    }

    private async Task SelectTracks(
        Channel channel,
        PlayoutItem playoutItem,
        MediaItemAudioVersion audioVersion,
        Core.Next.PlayoutItem nextPlayoutItem,
        string preferredAudioLanguage,
        string preferredAudioTitle,
        string preferredSubtitleLanguage,
        ChannelSubtitleMode subtitleMode,
        Option<List<Subtitle>> subtitles,
        bool shouldLogMessages,
        CancellationToken cancellationToken)
    {
        List<Subtitle> allSubtitles = await subtitles.IfNoneAsync(
            await GetSubtitles(channel, audioVersion.MediaItem, playoutItem.Id, playoutItem.InPoint));

        // TODO: external image subtitles
        allSubtitles.RemoveAll(s => s.IsImage && s.SubtitleKind is not SubtitleKind.Embedded);

        Option<MediaStream> maybeAudioStream = Option<MediaStream>.None;
        Option<Subtitle> maybeSubtitle = Option<Subtitle>.None;

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Custom)
        {
            StreamSelectorResult result = await customStreamSelector.SelectStreams(
                channel,
                nextPlayoutItem.Start,
                audioVersion,
                allSubtitles,
                shouldLogMessages);
            maybeAudioStream = result.AudioStream;
            maybeSubtitle = result.Subtitle;
        }

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Default || maybeAudioStream.IsNone)
        {
            maybeAudioStream =
                await ffmpegStreamSelector.SelectAudioStream(
                    audioVersion,
                    channel.StreamingMode,
                    channel,
                    preferredAudioLanguage,
                    preferredAudioTitle,
                    shouldLogMessages,
                    cancellationToken);

            maybeSubtitle =
                await ffmpegStreamSelector.SelectSubtitleStream(
                    allSubtitles.ToImmutableList(),
                    channel,
                    preferredSubtitleLanguage,
                    subtitleMode,
                    shouldLogMessages,
                    cancellationToken);
        }

        foreach (MediaStream audioStream in maybeAudioStream)
        {
            if (nextPlayoutItem.Tracks?.Audio?.StreamIndex is null)
            {
                nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                nextPlayoutItem.Tracks.Audio ??= new Core.Next.TrackSelection();
                nextPlayoutItem.Tracks.Audio.StreamIndex = audioStream.Index;
            }
        }

        foreach (Subtitle subtitle in maybeSubtitle)
        {
            if (subtitle.SubtitleKind is SubtitleKind.Embedded)
            {
                if (subtitle.IsImage)
                {
                    if (nextPlayoutItem.Tracks?.Subtitle?.StreamIndex is null)
                    {
                        nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                        nextPlayoutItem.Tracks.Subtitle ??= new Core.Next.TrackSelection();
                        nextPlayoutItem.Tracks.Subtitle.StreamIndex = subtitle.StreamIndex;
                    }
                }
                // next only supports sidecar text subtitles at the moment; ignore non-extracted text subs
                else if (subtitle.IsExtracted && !string.IsNullOrWhiteSpace(subtitle.Path))
                {
                    if (nextPlayoutItem.Tracks?.Subtitle?.Source is null)
                    {
                        nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                        nextPlayoutItem.Tracks.Subtitle ??= new Core.Next.TrackSelection();
                        nextPlayoutItem.Tracks.Subtitle.Source = new Core.Next.Source
                        {
                            SourceType = Core.Next.SourceType.Local,
                            Path = Path.Combine(FileSystemLayout.SubtitleCacheFolder, subtitle.Path),
                        };

                        SetInOutPoints(playoutItem, nextPlayoutItem.Tracks.Subtitle.Source);
                    }
                }
            }
            else if (!IsRemoteUri(subtitle.Path))
            {
                if (nextPlayoutItem.Tracks?.Subtitle?.Source is null)
                {
                    nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                    nextPlayoutItem.Tracks.Subtitle ??= new Core.Next.TrackSelection();
                    nextPlayoutItem.Tracks.Subtitle.Source = new Core.Next.Source
                    {
                        SourceType = Core.Next.SourceType.Local,
                        Path = subtitle.Path,
                    };

                    SetInOutPoints(playoutItem, nextPlayoutItem.Tracks.Subtitle.Source);
                }
            }
            else if (subtitle.Path.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
            {
                if (nextPlayoutItem.Tracks?.Subtitle?.Source is null)
                {
                    nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                    nextPlayoutItem.Tracks.Subtitle ??= new Core.Next.TrackSelection();
                    nextPlayoutItem.Tracks.Subtitle.Source = new Core.Next.Source
                    {
                        SourceType = Core.Next.SourceType.Http,
                        Uri = subtitle.Path,
                        KeepAlive = false,
                        Reconnect = true
                    };
                }
            }
        }
    }

    private void SelectGraphics(
        Option<ChannelWatermark> maybeGlobalWatermark,
        Channel channel,
        PlayoutItem playoutItem,
        Core.Next.PlayoutItem nextPlayoutItem,
        string frameRate,
        bool shouldLogMessages)
    {
        nextPlayoutItem.Graphics ??= [];

        List<WatermarkOptions> watermarks = watermarkSelector.SelectWatermarks(
            maybeGlobalWatermark,
            channel,
            playoutItem,
            playoutItem.StartOffset,
            shouldLogMessages: false);

        List<PlayoutItemGraphicsElement> graphicsElements = graphicsElementSelector.SelectGraphicsElements(
            channel,
            playoutItem,
            playoutItem.StartOffset,
            shouldLogMessages);

        if (watermarks.Count == 0 && graphicsElements.Count == 0)
        {
            return;
        }

        var outputFrameSize = new Resolution
        {
            Width = channel.FFmpegProfile.Resolution.Width,
            Height = channel.FFmpegProfile.Resolution.Height,
        };

        DateTimeOffset exp = playoutItem.FinishOffset + TimeSpan.FromHours(2);

        string sig = InternalUrlSigner.Sign(exp, "graphics", channel.Number, $"{playoutItem.Id}");

        var layer = new Core.Next.GraphicsLayer
        {
            Kind = Core.Next.GraphicsLayerKind.Canvas,
            Location = Core.Next.GraphicsLocation.TopLeft,
            Source = new Core.Next.PlayoutItemSource
            {
                SourceType = Core.Next.SourceType.Http,
                Uri =
                    $"http://localhost:{Settings.StreamingPort}/internal/graphics/{channel.Number}/{playoutItem.Id}?exp={exp.ToUnixTimeSeconds()}&sig={sig}",
                Reconnect = false,
                ProbeHint = new Core.Next.ProbeHint
                {
                    FormatName = "nut",
                    Video =
                    [
                        new Core.Next.VideoHint
                        {
                            Codec = "ffv1",
                            Width = outputFrameSize.Width,
                            Height = outputFrameSize.Height,
                            PixFmt = "bgra",
                            StreamIndex = 0,
                            FrameRate = frameRate,
                        }
                    ]
                }
            }
        };

        nextPlayoutItem.Graphics.Clear();
        nextPlayoutItem.Graphics.Add(layer);
    }

    private static async Task<List<Subtitle>> GetSubtitles(
        Channel channel,
        MediaItem mediaItem,
        int playoutItemId,
        TimeSpan playoutItemInPoint)
    {
        List<Subtitle> allSubtitles = mediaItem switch
        {
            Episode episode => await Optional(episode.EpisodeMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            Movie movie => await Optional(movie.MovieMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            MusicVideo => GetMusicVideoSubtitles(channel, playoutItemId, playoutItemInPoint),
            OtherVideo otherVideo => await Optional(otherVideo.OtherVideoMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            _ => []
        };

        bool isMediaServer = mediaItem is PlexMovie or PlexEpisode or
            JellyfinMovie or JellyfinEpisode or EmbyMovie or EmbyEpisode;

        if (isMediaServer)
        {
            // closed captions are currently unsupported
            allSubtitles.RemoveAll(s => s.Codec == "eia_608");
        }

        return allSubtitles;
    }

    private static List<Subtitle> GetMusicVideoSubtitles(
        Channel channel,
        int playoutItemId,
        TimeSpan playoutItemInPoint)
    {
        if (channel.MusicVideoCreditsMode is not ChannelMusicVideoCreditsMode.GenerateSubtitles)
        {
            return [];
        }

        string seekToMs = playoutItemInPoint > TimeSpan.Zero
            ? $"?seekToMs={(long)playoutItemInPoint.TotalMilliseconds}"
            : string.Empty;

        return
        [
            new Subtitle
            {
                Codec = "ass",
                Default = true,
                Forced = true,
                IsExtracted = false,
                SubtitleKind = SubtitleKind.Generated,
                Path =
                    $"http://localhost:{Settings.StreamingPort}/internal/ffmpeg/music-video-credits/{playoutItemId}{seekToMs}",
                SDH = false
            }
        ];
    }

    private static void SetInOutPoints(PlayoutItem playoutItem, Core.Next.Source source)
    {
        if (playoutItem is not DynamicPlayoutItem)
        {
            if (playoutItem.InPoint > TimeSpan.Zero)
            {
                source.InPointMs = (long)playoutItem.InPoint.TotalMilliseconds;
            }

            var duration = playoutItem.MediaItem.GetDurationForPlayout();
            if (playoutItem.OutPoint > TimeSpan.Zero && playoutItem.OutPoint < duration)
            {
                source.OutPointMs = (long)playoutItem.OutPoint.TotalMilliseconds;
            }
        }
    }

    private static bool IsRemoteUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out Uri uriResult)
        && (uriResult.Scheme == Uri.UriSchemeHttp ||
            uriResult.Scheme == Uri.UriSchemeHttps);
}
