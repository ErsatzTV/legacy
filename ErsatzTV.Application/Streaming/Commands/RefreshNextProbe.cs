using ErsatzTV.Core.Interfaces.Streaming;

namespace ErsatzTV.Application.Streaming;

public record RefreshNextProbe : IRequest<Option<NextProbeResult>>;
