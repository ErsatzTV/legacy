using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Capabilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using File = TagLib.File;

namespace ErsatzTV.Infrastructure.Metadata;

public partial class LocalStatisticsProvider : ILocalStatisticsProvider
{
    private readonly IHardwareCapabilitiesFactory _hardwareCapabilitiesFactory;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILogger<LocalStatisticsProvider> _logger;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IFileSystem _fileSystem;

    public LocalStatisticsProvider(
        IMetadataRepository metadataRepository,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        IHardwareCapabilitiesFactory hardwareCapabilitiesFactory,
        ILogger<LocalStatisticsProvider> logger)
    {
        _metadataRepository = metadataRepository;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _hardwareCapabilitiesFactory = hardwareCapabilitiesFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, MediaVersion>> GetStatistics(string ffprobePath, string path)
    {
        Either<BaseError, FFprobe> maybeProbe = await GetProbeOutput(ffprobePath, path);
        return maybeProbe.Match(
            ffprobe => ProjectToMediaVersion(path, ffprobe),
            Left<BaseError, MediaVersion>);
    }

    public async Task<Either<BaseError, bool>> RefreshStatistics(
        string ffmpegPath,
        string ffprobePath,
        MediaItem mediaItem)
    {
        try
        {
            string filePath = await PathForMediaItem(mediaItem);

            if (Path.GetExtension(filePath) == ".avs" && !_hardwareCapabilitiesFactory.IsAviSynthInstalled())
            {
                return BaseError.New(".avs files are not supported; compatible ffmpeg and avisynth are both required");
            }

            return await RefreshStatistics(ffmpegPath, ffprobePath, mediaItem, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh statistics for media item {Id}", mediaItem.Id);
            return BaseError.New(ex.Message);
        }
    }

    public Either<BaseError, List<SongTag>> GetSongTags(MediaItem mediaItem)
    {
        try
        {
            string mediaItemPath = mediaItem.GetHeadVersion().MediaFiles.Head().Path;

            // aifc is unsupported here
            string extension = Path.GetExtension(mediaItemPath);
            if (extension.Contains("aifc", StringComparison.OrdinalIgnoreCase))
            {
                return new List<SongTag>();
            }

            var song = File.Create(mediaItemPath);

            var result = new List<SongTag>();

            // album
            if (!string.IsNullOrWhiteSpace(song.Tag.Album))
            {
                result.Add(new SongTag(MetadataSongTag.Album, song.Tag.Album));
            }

            // album artist(s)
            IEnumerable<string> albumArtists = song.Tag.AlbumArtists.Filter(a => !string.IsNullOrWhiteSpace(a));
            result.AddRange(albumArtists.Map(albumArtist => new SongTag(MetadataSongTag.AlbumArtist, albumArtist)));

            // artist(s)
            IEnumerable<string> artists = song.Tag.Performers.Filter(p => !string.IsNullOrWhiteSpace(p));
            result.AddRange(artists.Map(artist => new SongTag(MetadataSongTag.Artist, artist)));

            // genre(s)
            IEnumerable<string> genres = song.Tag.Genres.Filter(g => !string.IsNullOrWhiteSpace(g));
            result.AddRange(genres.Map(genre => new SongTag(MetadataSongTag.Genre, genre)));

            // title
            if (!string.IsNullOrWhiteSpace(song.Tag.Title))
            {
                result.Add(new SongTag(MetadataSongTag.Title, song.Tag.Title));
            }

            if (!string.IsNullOrWhiteSpace(song.Tag.Comment))
            {
                result.Add(new SongTag(MetadataSongTag.Comment, song.Tag.Comment));
            }

            // track
            result.Add(new SongTag(MetadataSongTag.Track, song.Tag.Track.ToString(CultureInfo.InvariantCulture)));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get format tags for media item {Id}", mediaItem.Id);
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Option<double>> GetInterlacedRatio(
        string ffmpegPath,
        MediaItem mediaItem,
        CancellationToken cancellationToken)
    {
        try
        {
            string filePath = await PathForMediaItem(mediaItem);

            if (Path.GetExtension(filePath) == ".avs" && !_hardwareCapabilitiesFactory.IsAviSynthInstalled())
            {
                return Option<double>.None;
            }

            if (filePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                !_fileSystem.File.Exists(filePath))
            {
                _logger.LogDebug("Skipping interlaced ratio check for remote content");
                return Option<double>.None;
            }

            var duration = mediaItem.GetDurationForPlayout();
            if (duration < TimeSpan.FromSeconds(3))
            {
                return Option<double>.None;
            }

            Option<IdetStatistics> maybeStats = await GetIdetOutput(ffmpegPath, filePath, duration / 3);
            foreach (var stats in maybeStats)
            {
                if (stats.TotalFrames == 0)
                {
                    return 0;
                }

                return (double)stats.TotalInterlacedFrames / stats.TotalFrames;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check interlaced ratio for media item {Id}", mediaItem.Id);
        }

        return Option<double>.None;
    }

    public async Task<Option<int>> GetProfileCount(
        string ffmpegPath,
        MediaItem mediaItem,
        CancellationToken cancellationToken)
    {
        try
        {
            string filePath = await PathForMediaItem(mediaItem);
            return await GetProfileCount(ffmpegPath, filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check profile count for media item {Id}", mediaItem.Id);
        }

        return Option<int>.None;
    }

    private async Task<Either<BaseError, bool>> RefreshStatistics(
        string ffmpegPath,
        string ffprobePath,
        MediaItem mediaItem,
        string mediaItemPath)
    {
        try
        {
            Either<BaseError, FFprobe> maybeProbe = await GetProbeOutput(ffprobePath, mediaItemPath);
            return await maybeProbe.Match(
                async ffprobe =>
                {
                    MediaVersion version = ProjectToMediaVersion(mediaItemPath, ffprobe);
                    if (mediaItem is not Image and not RemoteStream && version.Duration.TotalSeconds < 1)
                    {
                        await AnalyzeDuration(ffmpegPath, mediaItemPath, version);
                    }

                    bool result = await ApplyVersionUpdate(mediaItem, version, mediaItemPath);
                    return Right<BaseError, bool>(result);
                },
                error => Task.FromResult(Left<BaseError, bool>(error)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh statistics for media item {Id}", mediaItem.Id);
            return BaseError.New(ex.Message);
        }
    }

    private async Task<bool> ApplyVersionUpdate(MediaItem mediaItem, MediaVersion version, string filePath)
    {
        MediaVersion mediaItemVersion = mediaItem.GetHeadVersion();

        bool durationChange = mediaItemVersion.Duration != version.Duration;

        version.DateUpdated = _localFileSystem.GetLastWriteTime(filePath);

        return await _metadataRepository.UpdateStatistics(mediaItem, version) && durationChange;
    }

    private static async Task<Either<BaseError, FFprobe>> GetProbeOutput(string ffprobePath, string filePath)
    {
        string[] arguments =
        [
            "-hide_banner",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-show_chapters",
            "-i", filePath
        ];

        //_logger.LogDebug("ffprobe arguments {FFProbeArguments}", arguments.ToList());

        BufferedCommandResult probe = await Cli.Wrap(ffprobePath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        if (probe.ExitCode != 0)
        {
            return BaseError.New($"FFprobe at {ffprobePath} exited with code {probe.ExitCode}");
        }

        FFprobe ffprobe = JsonConvert.DeserializeObject<FFprobe>(probe.StandardOutput);
        if (ffprobe is not null)
        {
            Match match = SarDarRegex().Match(probe.StandardError);
            if (match.Success)
            {
                string sar = match.Groups[1].Value;
                string dar = match.Groups[2].Value;
                foreach (FFprobeStreamData stream in Optional(
                                 ffprobe.streams?.Where(s => s.codec_type == "video").ToList())
                             .Flatten())
                {
                    FFprobeStreamData replacement = stream with
                    {
                        sample_aspect_ratio = sar, display_aspect_ratio = dar
                    };
                    ffprobe.streams?.Remove(stream);
                    ffprobe.streams?.Add(replacement);
                }
            }

            // fix chapter ids to be something sensible
            var maybeChapters = Optional(ffprobe.chapters).Flatten().ToList();
            var newChapters = new List<FFprobeChapter>();
            for (var index = 0; index < maybeChapters.Count; index++)
            {
                FFprobeChapter chapter = maybeChapters[index];
                newChapters.Add(chapter with { id = index });
            }

            return ffprobe with { chapters = newChapters };
        }

        return BaseError.New("Unable to deserialize ffprobe output");
    }

    private async Task<Option<IdetStatistics>> GetIdetOutput(string ffmpegPath, string filePath, TimeSpan seek)
    {
        string[] arguments =
        [
            "-hide_banner",
            "-ss", seek.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", filePath,
            "-vf", "idet",
            "-frames:v", "200",
            "-an",
            "-f", "null", "-"
        ];

        BufferedCommandResult idet = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        if (idet.ExitCode != 0)
        {
            _logger.LogInformation(
                "FFmpeg idet with arguments {Arguments} exited with code {ExitCode}",
                arguments,
                idet.ExitCode);

            return Option<IdetStatistics>.None;
        }

        var stats = new IdetStatistics();

        //_logger.LogDebug("StdErr: {Match}", idet.StandardError);

        var singleMatch = SingleFrameRegex().Matches(idet.StandardError).LastOrDefault();
        if (singleMatch?.Success == true)
        {
            _logger.LogDebug("Matched single frame: {Match}", singleMatch.Value);
            stats.SingleTff = int.Parse(singleMatch.Groups[1].Value, NumberFormatInfo.InvariantInfo);
            stats.SingleBff = int.Parse(singleMatch.Groups[2].Value, NumberFormatInfo.InvariantInfo);
            stats.SingleProgressive = int.Parse(singleMatch.Groups[3].Value, NumberFormatInfo.InvariantInfo);
        }

        var multiMatch = MultiFrameRegex().Matches(idet.StandardError).LastOrDefault();
        if (multiMatch?.Success == true)
        {
            _logger.LogDebug("Matched multi frame: {Match}", multiMatch.Value);
            stats.MultiTff = int.Parse(multiMatch.Groups[1].Value, NumberFormatInfo.InvariantInfo);
            stats.MultiBff = int.Parse(multiMatch.Groups[2].Value, NumberFormatInfo.InvariantInfo);
            stats.MultiProgressive = int.Parse(multiMatch.Groups[3].Value, NumberFormatInfo.InvariantInfo);
        }

        return stats;
    }

    private async Task<Option<int>> GetProfileCount(
        string ffmpegPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        // only scans first 30 seconds
        string[] arguments =
        [
            "-hide_banner",
            "-t", "30",
            "-i", filePath,
            "-c", "copy",
            "-bsf:v", "trace_headers",
            "-f", "null", "-"
        ];

        var uniqueProfiles = new System.Collections.Generic.HashSet<string>();

        // cancel as soon as we find a second distinct profile_idc
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            CommandResult traceHeaders = await Cli.Wrap(ffmpegPath)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(
                    PipeTarget.ToDelegate(line =>
                    {
                        int idx = line.IndexOf("profile_idc", StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            uniqueProfiles.Add(line[idx..]);
                            if (uniqueProfiles.Count > 1)
                            {
                                linkedCts.Cancel();
                            }
                        }
                    }))
                .ExecuteAsync(linkedCts.Token);

            if (traceHeaders.ExitCode != 0)
            {
                _logger.LogInformation(
                    "FFmpeg trace headers with arguments {Arguments} exited with code {ExitCode}",
                    arguments,
                    traceHeaders.ExitCode);

                return Option<int>.None;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // nothing to do here - canceled because a second profile was found
        }

        return uniqueProfiles.Count;
    }


    private async Task AnalyzeDuration(string ffmpegPath, string path, MediaVersion version)
    {
        try
        {
            _logger.LogInformation(
                "Media item at {Path} is missing duration metadata and requires additional analysis",
                path);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("-");

            using var probe = new Process();
            probe.StartInfo = startInfo;

            probe.Start();
            string output = await probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            if (probe.ExitCode == 0)
            {
                const string PATTERN = @"time=([^ ]+)";
                IEnumerable<string> reversed = output.Split("\n").Reverse();
                foreach (string line in reversed)
                {
                    Match match = Regex.Match(line, PATTERN);
                    if (match.Success)
                    {
                        string time = match.Groups[1].Value;
                        var duration = TimeSpan.Parse(time, NumberFormatInfo.InvariantInfo);
                        _logger.LogInformation("Analyzed duration is {Duration:hh\\:mm\\:ss}", duration);
                        version.Duration = duration;
                        return;
                    }
                }
            }
            else
            {
                _logger.LogError("Duration analysis failed for media item at {Path}", path);
            }
        }
        catch (Exception)
        {
            _logger.LogError("Duration analysis failed for media item at {Path}", path);
        }
    }

    public MediaVersion ProjectToMediaVersion(string path, FFprobe probeOutput) =>
        Optional(probeOutput)
            .Filter(json => json is { format: not null, streams: not null })
            .ToValidation<BaseError>("Unable to parse ffprobe output")
            .ToEither<FFprobe>()
            .Match(
                json =>
                {
                    var version = new MediaVersion
                    {
                        Name = "Main",
                        DateAdded = DateTime.UtcNow,
                        Streams = new List<MediaStream>(),
                        Chapters = new List<MediaChapter>()
                    };

                    if (double.TryParse(
                            json.format?.duration,
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out double duration))
                    {
                        var seconds = TimeSpan.FromSeconds(duration);
                        version.Duration = seconds;
                    }
                    // else
                    // {
                    //     _logger.LogWarning(
                    //         "Media item at {Path} has a missing or invalid duration {Duration} and will cause scheduling issues",
                    //         path,
                    //         json.format.duration);
                    // }

                    foreach (FFprobeStreamData audioStream in json.streams.Filter(s => s.codec_type == "audio"))
                    {
                        var stream = new MediaStream
                        {
                            MediaVersionId = version.Id,
                            MediaStreamKind = MediaStreamKind.Audio,
                            Index = audioStream.index,
                            Codec = audioStream.codec_name,
                            Profile = (audioStream.profile ?? string.Empty).ToLowerInvariant(),
                            Channels = audioStream.channels
                        };

                        if (audioStream.disposition is not null)
                        {
                            stream.Default = audioStream.disposition.@default == 1;
                            stream.Forced = audioStream.disposition.forced == 1;
                        }

                        if (audioStream.tags is not null)
                        {
                            stream.Language = audioStream.tags.language;
                            stream.Title = audioStream.tags.title;
                        }

                        version.Streams.Add(stream);
                    }

                    FFprobeStreamData videoStream = json.streams?
                        .Where(s => s.codec_type == "video")
                        .OrderByDescending(ParseBitRate)
                        .FirstOrDefault();
                    if (videoStream != null)
                    {
                        version.SampleAspectRatio = string.IsNullOrWhiteSpace(videoStream.sample_aspect_ratio)
                            ? "1:1"
                            : videoStream.sample_aspect_ratio;
                        version.DisplayAspectRatio = videoStream.display_aspect_ratio;
                        version.Width = videoStream.width;
                        version.Height = videoStream.height;
                        version.VideoScanKind = ScanKindFromFieldOrder(videoStream.field_order);
                        version.InterlacedRatio = null;
                        version.RFrameRate = videoStream.r_frame_rate;

                        var stream = new MediaStream
                        {
                            MediaVersionId = version.Id,
                            MediaStreamKind = MediaStreamKind.Video,
                            Index = videoStream.index,
                            Codec = videoStream.codec_name,
                            Profile = (videoStream.profile ?? string.Empty).ToLowerInvariant(),
                            PixelFormat = (videoStream.pix_fmt ?? string.Empty).ToLowerInvariant(),
                            ColorRange = (videoStream.color_range ?? string.Empty).ToLowerInvariant(),
                            ColorSpace = (videoStream.color_space ?? string.Empty).ToLowerInvariant(),
                            ColorTransfer = (videoStream.color_transfer ?? string.Empty).ToLowerInvariant(),
                            ColorPrimaries = (videoStream.color_primaries ?? string.Empty).ToLowerInvariant()
                        };

                        if (int.TryParse(videoStream.bits_per_raw_sample, out int bitsPerRawSample))
                        {
                            stream.BitsPerRawSample = bitsPerRawSample;
                        }

                        if (videoStream.disposition is not null)
                        {
                            stream.Default = videoStream.disposition.@default == 1;
                            stream.Forced = videoStream.disposition.forced == 1;
                            stream.AttachedPic = videoStream.disposition.attached_pic == 1;
                        }

                        version.Streams.Add(stream);
                    }

                    foreach (FFprobeStreamData subtitleStream in json.streams.Filter(s => s.codec_type == "subtitle"))
                    {
                        var stream = new MediaStream
                        {
                            MediaVersionId = version.Id,
                            MediaStreamKind = MediaStreamKind.Subtitle,
                            Index = subtitleStream.index,
                            Codec = subtitleStream.codec_name
                        };

                        if (subtitleStream.disposition is not null)
                        {
                            stream.Default = subtitleStream.disposition.@default == 1;
                            stream.Forced = subtitleStream.disposition.forced == 1;
                        }

                        if (subtitleStream.tags is not null)
                        {
                            stream.Language = subtitleStream.tags.language;
                            stream.Title = subtitleStream.tags.title;
                        }

                        version.Streams.Add(stream);
                    }

                    foreach (FFprobeStreamData attachmentStream in
                             json.streams.Filter(s => s.codec_type == "attachment"))
                    {
                        var stream = new MediaStream
                        {
                            MediaVersionId = version.Id,
                            MediaStreamKind = MediaStreamKind.Attachment,
                            Index = attachmentStream.index,
                            Codec = attachmentStream.codec_name
                        };

                        if (attachmentStream.tags is not null)
                        {
                            stream.FileName = attachmentStream.tags.filename;
                            stream.MimeType = attachmentStream.tags.mimetype;
                        }

                        version.Streams.Add(stream);
                    }

                    foreach (FFprobeChapter probedChapter in Optional(json.chapters).Flatten())
                    {
                        if (double.TryParse(
                                probedChapter.start_time,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out double startTime)
                            && double.TryParse(
                                probedChapter.end_time,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out double endTime))
                        {
                            var chapter = new MediaChapter
                            {
                                MediaVersionId = version.Id,
                                ChapterId = probedChapter.id,
                                StartTime = TimeSpan.FromSeconds(startTime),
                                EndTime = TimeSpan.FromSeconds(endTime),
                                Title = probedChapter.tags?.title
                            };

                            version.Chapters.Add(chapter);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Media item at {Path} has a missing or invalid chapter start/end time",
                                path);
                        }
                    }

                    if (version.Chapters.Count != 0)
                    {
                        MediaChapter last = version.Chapters.Last();
                        if (last.EndTime != version.Duration)
                        {
                            last.EndTime = version.Duration;
                        }
                    }

                    return version;
                },
                _ => new MediaVersion
                {
                    Name = "Main",
                    DateAdded = DateTime.UtcNow,
                    Streams = new List<MediaStream>(),
                    Chapters = new List<MediaChapter>()
                });

    private static VideoScanKind ScanKindFromFieldOrder(string fieldOrder) =>
        fieldOrder?.ToLowerInvariant() switch
        {
            "tt" or "bb" or "tb" or "bt" => VideoScanKind.Interlaced,
            "progressive" => VideoScanKind.Progressive,
            _ => VideoScanKind.Unknown
        };

    private static Task<string> PathForMediaItem(MediaItem mediaItem)
    {
        string path = mediaItem.GetHeadVersion().MediaFiles.Head().Path;

        if (mediaItem is RemoteStream remoteStream)
        {
            path = !string.IsNullOrWhiteSpace(remoteStream.Url)
                ? remoteStream.Url
                : $"http://localhost:{Settings.StreamingPort}/ffmpeg/remote-stream/{remoteStream.Id}";
        }

        return Task.FromResult(path);
    }

    private static long ParseBitRate(FFprobeStreamData stream)
    {
        if (long.TryParse(stream.bit_rate, out long result))
        {
            return result;
        }

        if (stream.tags?.variantBitrate is not null)
        {
            if (long.TryParse(stream.tags.variantBitrate, out result))
            {
                return result;
            }
        }

        return 0;
    }

    // ReSharper disable InconsistentNaming
    public record FFprobe(FFprobeFormat format, List<FFprobeStreamData> streams, List<FFprobeChapter> chapters);

    public record FFprobeFormat(string duration, FFprobeTags tags);

    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
    public record FFprobeDisposition(int @default, int forced, int attached_pic);

    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
    public record FFprobeStreamData(
        int index,
        string codec_name,
        string profile,
        string codec_type,
        int channels,
        int width,
        int height,
        string sample_aspect_ratio,
        string display_aspect_ratio,
        string pix_fmt,
        string color_range,
        string color_space,
        string color_transfer,
        string color_primaries,
        string field_order,
        string r_frame_rate,
        string bit_rate,
        string bits_per_raw_sample,
        FFprobeDisposition disposition,
        FFprobeTags tags);

    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
    public record FFprobeChapter(
        long id,
        string start_time,
        string end_time,
        FFprobeTags tags);

    public record FFprobeTags(
        string language,
        string title,
        string filename,
        string mimetype,
        string artist,
        [property: JsonProperty(PropertyName = "album_artist")]
        string albumArtist,
        string album,
        string track,
        string genre,
        string date,
        [property: JsonProperty(PropertyName = "variant_bitrate")]
        string variantBitrate)
    {
        public static readonly FFprobeTags Empty = new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }
    // ReSharper restore InconsistentNaming

    [GeneratedRegex(@"\[SAR\s+([0-9]+:[0-9]+)\s+DAR\s+([0-9]+:[0-9]+)\]")]
    private static partial Regex SarDarRegex();

    [GeneratedRegex(@"Single frame detection: TFF:\s+(\d+) BFF:\s+(\d+) Progressive:\s+(\d+)")]
    private static partial Regex SingleFrameRegex();

    [GeneratedRegex(@"Multi frame detection: TFF:\s+(\d+) BFF:\s+(\d+) Progressive:\s+(\d+)")]
    private static partial Regex MultiFrameRegex();

    private class IdetStatistics
    {
        public int SingleTff { get; set; }
        public int SingleBff { get; set; }
        public int SingleProgressive { get; set; }
        public int MultiTff { get; set; }
        public int MultiBff { get; set; }
        public int MultiProgressive { get; set; }

        public int TotalInterlacedFrames => SingleTff + SingleBff + MultiTff + MultiBff;
        public int TotalProgressiveFrames => SingleProgressive + MultiProgressive;
        public int TotalFrames => TotalInterlacedFrames + TotalProgressiveFrames;
    }
}
