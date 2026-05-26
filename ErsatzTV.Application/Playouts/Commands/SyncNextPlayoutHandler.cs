using System.Collections.Immutable;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CommandResult = CliWrap.CommandResult;
using PlayoutItem = ErsatzTV.Core.Domain.PlayoutItem;
using WatermarkLocation = ErsatzTV.FFmpeg.State.WatermarkLocation;

namespace ErsatzTV.Application.Playouts;

public partial class SyncNextPlayoutHandler(
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    ICustomStreamSelector customStreamSelector,
    IFFmpegStreamSelector ffmpegStreamSelector,
    IWatermarkSelector watermarkSelector,
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<SyncNextPlayoutHandler> logger)
    : IRequestHandler<SyncNextPlayout>
{
    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static partial int Rename(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string newpath
    );

    public async Task Handle(SyncNextPlayout request, CancellationToken cancellationToken)
    {
        // gen new folder name
        string versionFolderName = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        string channelFolder = fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, request.ChannelNumber);
        string versionFolder = fileSystem.Path.Combine(channelFolder, versionFolderName);

        logger.LogDebug("versioned playout folder is {Folder}", versionFolder);

        localFileSystem.EnsureFolderExists(versionFolder);

        await WriteAllJsonTo(request.ChannelNumber, versionFolder, cancellationToken);

        string currentFolder = fileSystem.Path.Combine(channelFolder, "current");

        // re-point symlink/junction to new folder
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Directory.Exists(currentFolder))
            {
                var dirInfo = new DirectoryInfo(currentFolder);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    dirInfo.Delete();
                }
                else
                {
                    logger.LogError("Expected junction at {Folder} but found a real directory", currentFolder);
                    return;
                }
            }

            var stdErrBuffer = new StringBuilder();
            CommandResult command = await Cli.Wrap("cmd.exe")
                .WithArguments(["/c", "mklink", "/j", "current", versionFolderName])
                .WithWorkingDirectory(channelFolder)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            if (!command.IsSuccess)
            {
                logger.LogError("Failed to link current playout JSON folder: {Error}", stdErrBuffer);
            }
        }
        else
        {
            string tempLink = fileSystem.Path.Combine(
                FileSystemLayout.NextPlayoutsFolder,
                request.ChannelNumber,
                fileSystem.Path.GetRandomFileName());

            fileSystem.File.CreateSymbolicLink(tempLink, versionFolderName);
            _ = Rename(tempLink, currentFolder);
        }

        CleanOldVersions(
            fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, request.ChannelNumber),
            currentFolder);
    }

    private async Task WriteAllJsonTo(string channelNumber, string targetFolder, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        TimeSpan playoutOffset = TimeSpan.Zero;
        string mirrorChannelNumber = null;
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
            mirrorChannelNumber = channel.MirrorSourceChannel.Number;
            playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;
        }

        List<PlayoutItem> playoutItems = await dbContext.PlayoutItems
            .AsNoTracking()
            .Where(i => i.Playout.Channel.Number == (mirrorChannelNumber ?? channelNumber))

            // get playout deco
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)

            .Include(i => i.Watermarks)

            // get playout templates (and deco templates/decos)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)

            .Include(i => i.MediaItem)
            .ThenInclude(mi => mi.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).EpisodeMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).ImageMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MovieMetadata)
            .ThenInclude(mm => mm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).RemoteStreamMetadata)
            .ThenInclude(em => em.Subtitles)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        logger.LogDebug("Located {Count} local playout items", playoutItems.Count);

        Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
            .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
            .BindT(watermarkId => dbContext.ChannelWatermarks
                .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

        foreach (IGrouping<DateTime, PlayoutItem> group in playoutItems.GroupBy(pi => pi.StartOffset.Date)
                     .Where(g => g.Any()))
        {
            var first = group.First();
            var last = group.Last();

            string fileName = fileSystem.Path.Combine(
                targetFolder,
                $"{first.StartOffset.ToUnixTimeMilliseconds()}_{last.FinishOffset.ToUnixTimeMilliseconds()}.json");

            var playout = new Core.Next.Playout { Version = "https://ersatztv.org/playout/version/0.0.1", Items = [] };
            foreach (PlayoutItem playoutItem in group)
            {
                if (playoutItem.MediaItem is not Episode && playoutItem.MediaItem is not Movie &&
                    playoutItem.MediaItem is not OtherVideo && playoutItem.MediaItem is not MusicVideo &&
                    playoutItem.MediaItem is not RemoteStream && playoutItem.MediaItem is not Image)
                {
                    continue;
                }

                MediaVersion headVersion = playoutItem.MediaItem.GetHeadVersion();

                playoutItem.Start += playoutOffset;
                playoutItem.Finish += playoutOffset;

                var nextPlayoutItem = new Core.Next.PlayoutItem
                {
                    Id = playoutItem.Id.ToString(CultureInfo.InvariantCulture),
                    Start = playoutItem.StartOffset,
                    Finish = playoutItem.FinishOffset
                };

                Option<Core.Next.Source> maybeSource = await SourceForItem(playoutItem, cancellationToken);
                if (maybeSource.IsNone)
                {
                    continue;
                }

                foreach (Core.Next.Source source in maybeSource)
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

                    nextPlayoutItem.Source = source;
                }

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
                                    Params = "anullsrc=channel_layout=stereo:sample_rate=48000"
                                }
                        },
                        Video = new Core.Next.TrackSelection
                        {
                            Source = videoSource
                        }
                    };
                }

                maybeChannel = await dbContext.Channels
                    .AsNoTracking()
                    .Include(c => c.Watermark)
                    .Include(c => c.Artwork)
                    .SingleOrDefaultAsync(c => c.Number == channelNumber, cancellationToken);
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
                        cancellationToken);
                    await SelectWatermark(
                        maybeGlobalWatermark,
                        channel,
                        playoutItem,
                        nextPlayoutItem);
                }

                playout.Items.Add(nextPlayoutItem);
            }

            await fileSystem.File.WriteAllTextAsync(fileName, Core.Next.Serialize.ToJson(playout), cancellationToken);
        }
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
        CancellationToken cancellationToken)
    {
        List<Subtitle> allSubtitles = await GetSubtitles(audioVersion.MediaItem, playoutItem.Id, playoutItem.InPoint);

        Option<MediaStream> maybeAudioStream = Option<MediaStream>.None;
        Option<Subtitle> maybeSubtitle = Option<Subtitle>.None;

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Custom)
        {
            StreamSelectorResult result = await customStreamSelector.SelectStreams(
                channel,
                nextPlayoutItem.Start,
                audioVersion,
                allSubtitles);
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
                    shouldLogMessages: false,
                    cancellationToken);

            maybeSubtitle =
                await ffmpegStreamSelector.SelectSubtitleStream(
                    allSubtitles.ToImmutableList(),
                    channel,
                    preferredSubtitleLanguage,
                    subtitleMode,
                    shouldLogMessages: false,
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
                if (nextPlayoutItem.Tracks?.Subtitle?.StreamIndex is null)
                {
                    nextPlayoutItem.Tracks ??= new Core.Next.PlayoutItemTracks();
                    nextPlayoutItem.Tracks.Subtitle ??= new Core.Next.TrackSelection();
                    nextPlayoutItem.Tracks.Subtitle.StreamIndex = subtitle.StreamIndex;
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

        // single, permanent or intermittent watermarks are supported
        if (watermarks.Count == 1 && watermarks.All(wm =>
                wm.Watermark.Mode is ChannelWatermarkMode.Permanent or ChannelWatermarkMode.Intermittent))
        {
            foreach (WatermarkOptions watermarkOptions in watermarks)
            {
                if (nextPlayoutItem.Watermark is null)
                {
                    Core.Next.WatermarkLocation location = watermarkOptions.Watermark.Location switch
                    {
                        WatermarkLocation.TopMiddle => Core.Next.WatermarkLocation.TopCenter,
                        WatermarkLocation.TopRight => Core.Next.WatermarkLocation.TopRight,
                        WatermarkLocation.LeftMiddle => Core.Next.WatermarkLocation.CenterLeft,
                        WatermarkLocation.MiddleCenter => Core.Next.WatermarkLocation.Center,
                        WatermarkLocation.RightMiddle => Core.Next.WatermarkLocation.CenterRight,
                        WatermarkLocation.BottomLeft => Core.Next.WatermarkLocation.BottomLeft,
                        WatermarkLocation.BottomMiddle => Core.Next.WatermarkLocation.BottomCenter,
                        WatermarkLocation.BottomRight => Core.Next.WatermarkLocation.BottomRight,
                        _ => Core.Next.WatermarkLocation.TopLeft,
                    };

                    nextPlayoutItem.Watermark = new Core.Next.Watermark
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
                        nextPlayoutItem.Watermark.WidthPercent = watermarkOptions.Watermark.WidthPercent;
                    }

                    if (watermarkOptions.ImagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        nextPlayoutItem.Watermark.Source = new Core.Next.PlayoutItemSource
                        {
                            SourceType = Core.Next.SourceType.Http,
                            Uri = watermarkOptions.ImagePath,
                        };
                    }
                    else
                    {
                        nextPlayoutItem.Watermark.Source = new Core.Next.PlayoutItemSource
                        {
                            SourceType = Core.Next.SourceType.Local,
                            Path = watermarkOptions.ImagePath,
                        };
                    }

                    if (watermarkOptions.Watermark.Mode is ChannelWatermarkMode.Intermittent)
                    {
                        nextPlayoutItem.Watermark.Timing = new Core.Next.Timing
                        {
                            TimingType = Core.Next.TimingType.Periodic,
                            Clock = Core.Next.PeriodicClock.Wall,
                            FrequencyMs = watermarkOptions.Watermark.FrequencyMinutes * 60 * 1000,
                            HoldMs = watermarkOptions.Watermark.DurationSeconds * 1000,
                        };
                    }
                }
            }
        }
    }

    private async Task<Option<Core.Next.Source>> SourceForItem(
        PlayoutItem playoutItem,
        CancellationToken cancellationToken)
    {
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

    private void CleanOldVersions(
        string playoutRoot,
        string currentLinkPath,
        int keepVersions = 2,
        TimeSpan? gracePeriod = null)
    {
        gracePeriod ??= TimeSpan.FromMinutes(5);

        string currentResolvedPath = null;
        if (Directory.Exists(currentLinkPath))
        {
            currentResolvedPath = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(currentLinkPath) ?? "",
                    Directory.ResolveLinkTarget(currentLinkPath, true)?.FullName ?? ""
                ));
        }

        var directories = Directory.GetDirectories(playoutRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => long.TryParse(d.Name, out _))
            .OrderByDescending(d => d.Name)
            .ToList();

        int keptCount = 0;

        foreach (var dir in directories)
        {
            string fullDir = dir.FullName;

            if (fullDir.Equals(currentResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                keptCount++;
                continue;
            }

            if (keptCount < keepVersions)
            {
                keptCount++;
                continue;
            }

            if (DateTime.Now - dir.LastWriteTime < gracePeriod)
            {
                continue;
            }

            try
            {
                dir.Delete(recursive: true);
                logger.LogDebug("Cleaned up old playout version: {Folder}", dir.Name);
            }
            catch (IOException)
            {
                // ignore errors; will be cleaned up next time through
                logger.LogDebug("Skipping busy folder: {Folder}", dir.Name);
            }
        }
    }

    private static async Task<List<Subtitle>> GetSubtitles(
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
            MusicVideo => GetMusicVideoSubtitles(playoutItemId, playoutItemInPoint),
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

        // TODO: external image subtitles
        allSubtitles.RemoveAll(s => s.IsImage && s.SubtitleKind is not SubtitleKind.Embedded);

        return allSubtitles;
    }

    private static List<Subtitle> GetMusicVideoSubtitles(int playoutItemId, TimeSpan playoutItemInPoint)
    {
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
}
