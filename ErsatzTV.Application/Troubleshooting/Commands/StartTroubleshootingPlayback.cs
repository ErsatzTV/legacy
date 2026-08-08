using ErsatzTV.Application.MediaItems;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Application.Troubleshooting;

public record StartTroubleshootingPlayback(
    Guid SessionId,
    StreamingEngine StreamingEngine,
    string StreamSelector,
    PlayoutItemResult PlayoutItemResult,
    Option<MediaItemInfo> MediaItemInfo,
    TroubleshootingInfo TroubleshootingInfo) : IRequest, IFFmpegWorkerRequest;
