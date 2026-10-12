using System.Reflection;

namespace ErsatzTV;

public static class NextIssueUrl
{
    private static readonly string ErsatzTVVersion =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    // query keys match field ids in next's .github/ISSUE_TEMPLATE/legacy.yml
    public static string Get() =>
        "https://github.com/ErsatzTV/next/issues/new?template=legacy.yml" +
        $"&ersatztv-version={Uri.EscapeDataString(ErsatzTVVersion)}" +
        $"&next-version={Uri.EscapeDataString(NextVersion.Version ?? "unknown")}";
}
