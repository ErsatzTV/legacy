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
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Scheduling;

public class PlayoutItemConverter(
    IFileSystem fileSystem,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    ICustomStreamSelector customStreamSelector,
    IFFmpegStreamSelector ffmpegStreamSelector,
    IWatermarkSelector watermarkSelector,
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
            Id = playoutItem is DynamicPlayoutItem ? Guid.NewGuid().ToString() : playoutItem.Id.ToString(CultureInfo.InvariantCulture),
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
                    PixFmt = string.IsNullOrWhiteSpace(s.PixelFormat) ? PixelFormatForBitDepth(s.BitsPerRawSample) : s.PixelFormat,
                    FrameRate = headVersion.RFrameRate,
                    SampleAspectRatio = headVersion.SampleAspectRatio,
                    DisplayAspectRatio = headVersion.DisplayAspectRatio,
                    ColorPrimaries = s.ColorPrimaries,
                    ColorRange = s.ColorRange,
                    ColorSpace = s.ColorSpace,
                    ColorTransfer = s.ColorTransfer
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
                                    Audio = [
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
                await SelectWatermark(
                    maybeGlobalWatermark,
                    channel,
                    playoutItem,
                    nextPlayoutItem);
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
        if (playoutItem is DynamicPlayoutItem)
        {
            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Dynamic,
                Uri = $"http://localhost:{Settings.StreamingPort}/media/fallback"
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
            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri = $"http://localhost:{Settings.StreamingPort}/media/plex/{mediaSourceId}/{pmf.Key}",
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
            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri = $"http://localhost:{Settings.StreamingPort}/media/jellyfin/{itemId}",
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
            return new Core.Next.Source
            {
                SourceType = Core.Next.SourceType.Http,
                Uri = $"http://localhost:{Settings.StreamingPort}/media/emby/{itemId}",
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
            else if (!subtitle.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
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

    private async Task SelectWatermark(
        Option<ChannelWatermark> maybeGlobalWatermark,
        Channel channel,
        PlayoutItem playoutItem,
        Core.Next.PlayoutItem nextPlayoutItem)
    {
        List<WatermarkOptions> watermarks = watermarkSelector.SelectWatermarks(
            maybeGlobalWatermark,
            channel,
            playoutItem,
            playoutItem.StartOffset,
            shouldLogMessages: false);

        // permanent or intermittent watermarks are supported
        if (watermarks.All(wm => wm.Watermark.Mode is ChannelWatermarkMode.Permanent or ChannelWatermarkMode.Intermittent))
        {
            nextPlayoutItem.Graphics = [];

            foreach (WatermarkOptions watermarkOptions in watermarks.OrderBy(wm => wm.Watermark.ZIndex))
            {
                Core.Next.GraphicsLocation location = watermarkOptions.Watermark.Location switch
                {
                    WatermarkLocation.TopMiddle => Core.Next.GraphicsLocation.TopCenter,
                    WatermarkLocation.TopRight => Core.Next.GraphicsLocation.TopRight,
                    WatermarkLocation.LeftMiddle => Core.Next.GraphicsLocation.CenterLeft,
                    WatermarkLocation.MiddleCenter => Core.Next.GraphicsLocation.Center,
                    WatermarkLocation.RightMiddle => Core.Next.GraphicsLocation.CenterRight,
                    WatermarkLocation.BottomLeft => Core.Next.GraphicsLocation.BottomLeft,
                    WatermarkLocation.BottomMiddle => Core.Next.GraphicsLocation.BottomCenter,
                    WatermarkLocation.BottomRight => Core.Next.GraphicsLocation.BottomRight,
                    _ => Core.Next.GraphicsLocation.TopLeft,
                };

                var layer = new Core.Next.GraphicsLayer
                {
                    Location = location,
                    HorizontalMarginPercent = watermarkOptions.Watermark.HorizontalMarginPercent,
                    VerticalMarginPercent = watermarkOptions.Watermark.VerticalMarginPercent,
                    OpacityPercent = watermarkOptions.Watermark.Opacity,
                    StreamIndex = await watermarkOptions.ImageStreamIndex.IfNoneAsync(0),
                    WithinSourceContent = watermarkOptions.Watermark.PlaceWithinSourceContent,
                };

                if (watermarkOptions.Watermark.Size is WatermarkSize.Scaled)
                {
                    layer.WidthPercent = watermarkOptions.Watermark.WidthPercent;
                }

                if (watermarkOptions.ImagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    layer.Source = new Core.Next.PlayoutItemSource
                    {
                        SourceType = Core.Next.SourceType.Http,
                        Uri = watermarkOptions.ImagePath,
                    };
                }
                else
                {
                    layer.Source = new Core.Next.PlayoutItemSource
                    {
                        SourceType = Core.Next.SourceType.Local,
                        Path = watermarkOptions.ImagePath,
                    };
                }

                if (watermarkOptions.Watermark.Mode is ChannelWatermarkMode.Intermittent)
                {
                    layer.Timing = new Core.Next.Timing
                    {
                        TimingType = Core.Next.TimingType.Periodic,
                        Clock = Core.Next.PeriodicClock.Wall,
                        FrequencyMs = watermarkOptions.Watermark.FrequencyMinutes * 60 * 1000,
                        HoldMs = watermarkOptions.Watermark.DurationSeconds * 1000,
                    };
                }

                nextPlayoutItem.Graphics.Add(layer);
            }
        }
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

    private static List<Subtitle> GetMusicVideoSubtitles(Channel channel, int playoutItemId, TimeSpan playoutItemInPoint)
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
                Path = $"http://localhost:{Settings.StreamingPort}/ffmpeg/music-video-credits/{playoutItemId}{seekToMs}",
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
}
