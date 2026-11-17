using Serilog;

namespace ErsatzTV;

public static class UnsupportedJwtConfig
{
    public const string Message =
        "JWT streaming authentication has been removed from ErsatzTV, and JWT:IssuerSigningKey " +
        "(JWT__ISSUERSIGNINGKEY) is still configured. All IPTV and HDHomeRun endpoints are disabled until " +
        "it is removed. ErsatzTV streams are not authenticated; for remote access use a VPN or a " +
        "reverse proxy that you configure.";

    private static long _lastLogTicks;

    public static bool IsConfigured { get; private set; }

    public static void Init(IConfiguration configuration) =>
        IsConfigured = !string.IsNullOrWhiteSpace(configuration["JWT:IssuerSigningKey"]);

    public static async Task BlockIptv(HttpContext context, Func<Task> next)
    {
        if (!IptvPaths.IsIptvPath(context.Request.Path))
        {
            await next();
            return;
        }

        if (ShouldLogBlockedRequest())
        {
            Log.Error("Blocked {Path}: {Message}", context.Request.Path.Value, Message);
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(Message);
    }

    // clients retry constantly, so blocked requests only re-log the error every ten minutes
    private static bool ShouldLogBlockedRequest()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastLogTicks);
        return (last == 0 || now - last >= TimeSpan.FromMinutes(10).TotalMilliseconds) &&
               Interlocked.CompareExchange(ref _lastLogTicks, now, last) == last;
    }
}
