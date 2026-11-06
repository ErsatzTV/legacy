using ErsatzTV.Core.Next.Description;

namespace ErsatzTV.Core.Interfaces.Streaming;

// Json: raw probe output for troubleshooting bundles
public record NextProbeResult(RuntimeCapability Capability, string Json, string FFmpegPath, DateTimeOffset ProbedAt);

public interface INextProbeService
{
    Option<NextProbeResult> Latest { get; }

    Task<Option<NextProbeResult>> Refresh(string ffmpegPath, CancellationToken cancellationToken);
}
