using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Core.Interfaces.Streaming;

public interface IGraphicsEngineContextFactory
{
    /// <summary>
    /// Builds a fully loaded engine context, or None when nothing would be rendered.
    /// </summary>
    Task<Option<GraphicsEngineContext>> Create(
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
        CancellationToken cancellationToken);
}
