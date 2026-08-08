using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Application.Troubleshooting;

public record PrepareTroubleshootingPlayback(
    Guid SessionId,
    StreamingMode StreamingMode,
    int MediaItemId,
    int ChannelId,
    StreamingEngine StreamingEngine,
    int FFmpegProfileId,
    string StreamSelector,
    List<int> WatermarkIds,
    List<int> GraphicsElementIds,
    int? SubtitleId,
    Option<int> SeekSeconds,
    Option<DateTimeOffset> Start)
    : IRequest<Either<BaseError, PlayoutItemResult>>;
