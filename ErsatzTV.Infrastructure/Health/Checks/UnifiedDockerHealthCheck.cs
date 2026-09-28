using System.Reflection;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;

namespace ErsatzTV.Infrastructure.Health.Checks;

public class UnifiedDockerHealthCheck : BaseHealthCheck, IUnifiedDockerHealthCheck
{
    private static readonly string InfoVersion =
        Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public override string Title => "Unified Docker";

    public Task<HealthCheckResult> Check(CancellationToken cancellationToken)
    {
        if (InfoVersion.Contains("docker-vaapi") || InfoVersion.Contains("docker-nvidia"))
        {
            return WarningResult(
                    "VAAPI and NVIDIA docker tag suffixes are deprecated; please remove `-vaapi` or `-nvidia` and pull the default image.",
                    "docker tag is deprecated")
                .AsTask();
        }

        return NotApplicableResult().AsTask();
    }
}
