using System.Runtime.InteropServices;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Capabilities.Nvidia;
using ErsatzTV.FFmpeg.Runtime;
using MediatR;
using Microsoft.Extensions.Caching.Memory;

namespace ErsatzTV.Services.RunOnce;

public class PlatformSettingsService(IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using IServiceScope scope = serviceScopeFactory.CreateScope();
        IRuntimeInfo runtimeInfo = scope.ServiceProvider.GetRequiredService<IRuntimeInfo>();
        if (runtimeInfo != null)
        {
            if (runtimeInfo.IsOSPlatform(OSPlatform.Linux) || runtimeInfo.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    NvEncSharpRedirector.Init();
                }
                catch (FileNotFoundException)
                {
                    // do nothing
                }
                catch (TypeInitializationException)
                {
                    // do nothing
                }
            }

            if (runtimeInfo.IsOSPlatform(OSPlatform.Linux))
            {
                if (Directory.Exists("/dev/dri"))
                {
                    ILocalFileSystem localFileSystem = scope.ServiceProvider.GetRequiredService<ILocalFileSystem>();
                    IMemoryCache memoryCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

                    var devices = localFileSystem.ListFiles("/dev/dri")
                        .Filter(s => s.StartsWith("/dev/dri/render", StringComparison.OrdinalIgnoreCase)
                                     || s.StartsWith("/dev/dri/card", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    memoryCache.Set("ffmpeg.render_devices", devices);
                }

                IHardwareCapabilitiesFactory hardwareCapabilitiesFactory =
                    scope.ServiceProvider.GetRequiredService<IHardwareCapabilitiesFactory>();
                if (hardwareCapabilitiesFactory != null)
                {
                    IMemoryCache memoryCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

                    List<string> displays = await hardwareCapabilitiesFactory.GetVaapiDisplays();
                    memoryCache.Set("ffmpeg.vaapi_displays", displays);
                }
            }

            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Option<Describe> maybeDescribe = await mediator.Send(new GetNextDescribe(), stoppingToken);
            foreach (Describe describe in maybeDescribe)
            {
                NextVersion.Version = describe.Binary.Version;
                Serilog.Log.Logger.Information(
                    "ErsatzTV Next version {Version} ({Commit})",
                    NextVersion.Version,
                    describe.Binary.Commit ?? "unknown commit");

                foreach (string problem in NextContract.Check(describe))
                {
                    Serilog.Log.Logger.Warning("ErsatzTV Next is not compatible: {Problem}", problem);
                }
            }

            if (maybeDescribe.IsNone)
            {
                NextVersion.Version = await mediator.Send(new GetNextVersion(), stoppingToken);
                Serilog.Log.Logger.Information("ErsatzTV Next version {Version}", NextVersion.Version);
                Serilog.Log.Logger.Warning(
                    "Unable to describe ErsatzTV Next; channels using the next streaming engine will not start");
            }
        }
    }
}
