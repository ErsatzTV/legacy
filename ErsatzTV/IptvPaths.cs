namespace ErsatzTV;

public static class IptvPaths
{
    public static bool IsIptvPath(PathString path) =>
        path.StartsWithSegments("/iptv") ||
        path.StartsWithSegments("/discover.json") ||
        path.StartsWithSegments("/device.xml") ||
        path.StartsWithSegments("/lineup.json") ||
        path.StartsWithSegments("/lineup_status.json");
}
