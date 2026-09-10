using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Streaming;

public record GetGraphicsCanvasStream(
    string ChannelNumber,
    int PlayoutItemId,
    TimeSpan Offset,
    TimeSpan Duration,
    FrameRate FrameRate) : IRequest<Option<Stream>>;
