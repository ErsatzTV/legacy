using ErsatzTV.Core.Interfaces.Streaming;

namespace ErsatzTV.Application.Streaming;

public record GetNextProbe : IRequest<Option<NextProbeResult>>;
