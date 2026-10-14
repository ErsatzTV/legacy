using ErsatzTV.Core.Graphics;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Streaming;

public record GetGraphicsCanvasStream(
    string ChannelNumber,
    int PlayoutItemId,
    TimeSpan Offset,
    TimeSpan Duration,
    FrameRate FrameRate,
    Option<CanvasRegion> Region) : IRequest<Option<Stream>>;
