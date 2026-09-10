using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class GraphicsEngineContextFactory(IGraphicsElementLoader graphicsElementLoader)
    : IGraphicsEngineContextFactory
{
    public async Task<Option<GraphicsEngineContext>> Create(
        Channel channel,
        MediaItem mediaItem,
        MediaVersion videoVersion,
        List<WatermarkOptions> watermarks,
        List<PlayoutItemGraphicsElement> elements,
        FrameRate frameRate,
        DateTimeOffset channelStartTime,
        DateTimeOffset contentStartTime,
        DateTimeOffset contentFinishTime,
        TimeSpan seek,
        TimeSpan duration,
        TimeSpan contentTotalDuration,
        CancellationToken cancellationToken)
    {
        if (watermarks.Count == 0 && elements.Count == 0)
        {
            return None;
        }

        List<GraphicsElementContext> elementContexts = [.. watermarks.Select(wm => new WatermarkElementContext(wm))];

        var context = new GraphicsEngineContext(
            channel.Number,
            mediaItem,
            elementContexts,
            TemplateVariables: [],
            SquarePixelFrameSize(channel.FFmpegProfile, videoVersion),
            channel.FFmpegProfile.Resolution,
            frameRate,
            channelStartTime,
            contentStartTime,
            contentFinishTime,
            seek,
            duration,
            contentTotalDuration);

        context = await graphicsElementLoader.LoadAll(context, elements, cancellationToken);

        return context?.Elements?.Count > 0 ? Some(context) : None;
    }

    // must match the content box ffmpeg produces: stretch and crop both fill the frame,
    // scale-and-pad leaves the square-pixel scaled size inside the padded frame
    private static Resolution SquarePixelFrameSize(FFmpegProfile profile, MediaVersion videoVersion)
    {
        if (profile.ScalingBehavior is ScalingBehavior.Stretch or ScalingBehavior.Crop)
        {
            return new Resolution { Width = profile.Resolution.Width, Height = profile.Resolution.Height };
        }

        var videoStream = new VideoStream(
            0,
            string.Empty,
            string.Empty,
            None,
            ColorParams.Unknown,
            new FrameSize(videoVersion.Width, videoVersion.Height),
            videoVersion.SampleAspectRatio,
            videoVersion.DisplayAspectRatio,
            None,
            StillImage: false,
            ScanKind.Progressive);

        FrameSize scaledSize = videoStream.SquarePixelFrameSize(
            new FrameSize(profile.Resolution.Width, profile.Resolution.Height));

        return new Resolution { Width = scaledSize.Width, Height = scaledSize.Height };
    }
}
