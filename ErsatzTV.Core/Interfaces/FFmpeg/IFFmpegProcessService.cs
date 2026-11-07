using CliWrap;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.State;

namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface IFFmpegProcessService
{
    Task<PlayoutItemResult> ForPlayoutItem(
        string ffmpegPath,
        string ffprobePath,
        bool saveReports,
        Channel channel,
        MediaItemVideoVersion videoVersion,
        MediaItemAudioVersion audioVersion,
        string videoPath,
        string audioPath,
        Func<FFmpegPlaybackSettings, Task<List<Subtitle>>> getSubtitles,
        string preferredAudioLanguage,
        string preferredAudioTitle,
        string preferredSubtitleLanguage,
        ChannelSubtitleMode subtitleMode,
        DateTimeOffset start,
        DateTimeOffset finish,
        DateTimeOffset now,
        TimeSpan originalContentDuration,
        List<WatermarkOptions> watermarks,
        List<PlayoutItemGraphicsElement> graphicsElements,
        string vaapiDisplay,
        VaapiDriver vaapiDriver,
        string vaapiDevice,
        Option<int> qsvExtraHardwareFrames,
        bool hlsRealtime,
        StreamInputKind streamInputKind,
        FillerKind fillerKind,
        TimeSpan inPoint,
        DateTimeOffset channelStartTime,
        TimeSpan ptsOffset,
        Option<FrameRate> targetFramerate,
        Option<string> customReportsFolder,
        Action<FFmpegPipeline> pipelineAction,
        bool canProxy,
        CancellationToken cancellationToken);

    Task<Command> ForError(
        string ffmpegPath,
        Channel channel,
        DateTimeOffset now,
        Option<TimeSpan> duration,
        string errorMessage,
        bool hlsRealtime,
        TimeSpan ptsOffset,
        string vaapiDisplay,
        VaapiDriver vaapiDriver,
        string vaapiDevice,
        Option<int> qsvExtraHardwareFrames,
        CancellationToken cancellationToken);

    Task<Command> Slug(
        string ffmpegPath,
        Channel channel,
        DateTimeOffset now,
        TimeSpan duration,
        bool hlsRealtime,
        TimeSpan ptsOffset,
        CancellationToken cancellationToken);

    Task<Command> WrapSegmenter(
        string ffmpegPath,
        bool saveReports,
        Channel channel,
        string scheme,
        string host,
        string accessToken,
        CancellationToken cancellationToken);

    Task<Command> ResizeImage(
        string ffmpegPath,
        string inputFile,
        string outputFile,
        int height,
        CancellationToken cancellationToken);

    Task<Either<BaseError, string>> GenerateSongImage(
        string ffmpegPath,
        string ffprobePath,
        Option<string> subtitleFile,
        Channel channel,
        MediaVersion videoVersion,
        string videoPath,
        bool boxBlur,
        Option<string> watermarkPath,
        WatermarkLocation watermarkLocation,
        int horizontalMarginPercent,
        int verticalMarginPercent,
        int watermarkWidthPercent,
        CancellationToken cancellationToken);

    Task<Command> SeekTextSubtitle(
        string ffmpegPath,
        string inputFile,
        string codec,
        TimeSpan seek,
        CancellationToken cancellationToken);
}
