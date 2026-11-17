using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public record StartFFmpegSession(
    string ChannelNumber,
    string Mode,
    string Scheme,
    string Host,
    string PathBase) :
    IRequest<Either<BaseError, string>>,
    IFFmpegWorkerRequest;
