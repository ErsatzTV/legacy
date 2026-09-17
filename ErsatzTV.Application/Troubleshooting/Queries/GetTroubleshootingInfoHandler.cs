using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Capabilities.Qsv;
using ErsatzTV.FFmpeg.Runtime;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ErsatzTV.Application.Troubleshooting.Queries;

public class GetTroubleshootingInfoHandler : IRequestHandler<GetTroubleshootingInfo, TroubleshootingInfo>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IHardwareCapabilitiesFactory _hardwareCapabilitiesFactory;
    private readonly IHealthCheckService _healthCheckService;
    private readonly IMemoryCache _memoryCache;
    private readonly IMediator _mediator;
    private readonly IRuntimeInfo _runtimeInfo;

    public GetTroubleshootingInfoHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IHealthCheckService healthCheckService,
        IHardwareCapabilitiesFactory hardwareCapabilitiesFactory,
        IConfigElementRepository configElementRepository,
        IRuntimeInfo runtimeInfo,
        IMemoryCache memoryCache,
        IMediator mediator)
    {
        _dbContextFactory = dbContextFactory;
        _healthCheckService = healthCheckService;
        _hardwareCapabilitiesFactory = hardwareCapabilitiesFactory;
        _configElementRepository = configElementRepository;
        _runtimeInfo = runtimeInfo;
        _memoryCache = memoryCache;
        _mediator = mediator;
    }

    public async Task<TroubleshootingInfo> Handle(GetTroubleshootingInfo request, CancellationToken cancellationToken)
    {
        List<HealthCheckResult> healthCheckResults = await _healthCheckService.PerformHealthChecks(cancellationToken);

        string version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        string ffmpegVersion = "unknown";

        var healthCheckSummaries = healthCheckResults
            .Filter(r => r.Status is HealthCheckStatus.Warning or HealthCheckStatus.Fail)
            .Map(r => new HealthCheckResultSummary(r.Title, r.Message))
            .ToList();

        FFmpegSettingsViewModel ffmpegSettings = await _mediator.Send(new GetFFmpegSettings(), cancellationToken);

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<Channel> channels = await dbContext.Channels
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<FFmpegProfile> ffmpegProfiles = await dbContext.FFmpegProfiles
            .AsNoTracking()
            .Include(p => p.Resolution)
            .ToListAsync(cancellationToken);

        List<ChannelWatermark> channelWatermarks = await dbContext.ChannelWatermarks
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        bool aviSynthDemuxer = false;
        bool aviSynthInstalled = false;

        string nvidiaCapabilities = null;
        StringBuilder qsvCapabilities = new();
        StringBuilder vaapiCapabilities = new();
        StringBuilder videoToolboxCapabilities = new();
        Option<ConfigElement> maybeFFmpegPath =
            await _configElementRepository.GetConfigElement(ConfigElementKey.FFmpegPath, cancellationToken);
        if (maybeFFmpegPath.IsNone)
        {
            nvidiaCapabilities = "Unable to locate ffmpeg";
        }
        else
        {
            foreach (ConfigElement ffmpegPath in maybeFFmpegPath)
            {
                ffmpegVersion =
                    await _hardwareCapabilitiesFactory.GetFFmpegVersion(ffmpegPath.Value, cancellationToken);

                nvidiaCapabilities = await _hardwareCapabilitiesFactory.GetNvidiaOutput(ffmpegPath.Value);

                if (!_memoryCache.TryGetValue("ffmpeg.render_devices", out List<string> vaapiDevices))
                {
                    vaapiDevices = ["/dev/dri/renderD128"];
                }

                if (!_memoryCache.TryGetValue("ffmpeg.vaapi_displays", out List<string> vaapiDisplays))
                {
                    vaapiDisplays = ["drm"];
                }

                foreach (string qsvDevice in vaapiDevices)
                {
                    QsvOutput output = await _hardwareCapabilitiesFactory.GetQsvOutput(ffmpegPath.Value, qsvDevice);
                    qsvCapabilities.AppendLine(CultureInfo.InvariantCulture, $"Checking device {qsvDevice}");
                    qsvCapabilities.AppendLine(CultureInfo.InvariantCulture, $"Exit Code: {output.ExitCode}");
                    qsvCapabilities.AppendLine();
                    qsvCapabilities.AppendLine(output.Output);
                    qsvCapabilities.AppendLine();
                }

                if (_runtimeInfo.IsOSPlatform(OSPlatform.Linux))
                {
                    var allDrivers = new List<VaapiDriver>
                        { VaapiDriver.iHD, VaapiDriver.i965, VaapiDriver.RadeonSI, VaapiDriver.Nouveau };

                    foreach (string display in vaapiDisplays)
                    foreach (VaapiDriver activeDriver in allDrivers)
                    foreach (string vaapiDevice in vaapiDevices)
                    {
                        foreach (string output in await _hardwareCapabilitiesFactory.GetVaapiOutput(
                                     display,
                                     Optional(GetDriverName(activeDriver)),
                                     vaapiDevice))
                        {
                            vaapiCapabilities.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"Checking display [{display}] driver [{activeDriver}] device [{vaapiDevice}]{Environment.NewLine}");
                            vaapiCapabilities.AppendLine();
                            vaapiCapabilities.AppendLine(output);
                            vaapiCapabilities.AppendLine();
                        }
                    }
                }

                if (_runtimeInfo.IsOSPlatform(OSPlatform.OSX))
                {
                    List<string> decoders = _hardwareCapabilitiesFactory.GetVideoToolboxDecoders();
                    videoToolboxCapabilities.AppendLine("VideoToolbox Decoders: ");
                    videoToolboxCapabilities.AppendLine();
                    foreach (string decoder in decoders)
                    {
                        videoToolboxCapabilities.AppendLine(CultureInfo.InvariantCulture, $"\t{decoder}");
                    }

                    videoToolboxCapabilities.AppendLine();
                    videoToolboxCapabilities.AppendLine();

                    List<string> encoders = _hardwareCapabilitiesFactory.GetVideoToolboxEncoders();
                    videoToolboxCapabilities.AppendLine("VideoToolbox Encoders: ");
                    videoToolboxCapabilities.AppendLine();
                    foreach (string encoder in encoders)
                    {
                        videoToolboxCapabilities.AppendLine(CultureInfo.InvariantCulture, $"\t{encoder}");
                    }

                    videoToolboxCapabilities.AppendLine();
                    videoToolboxCapabilities.AppendLine();
                }

                var ffmpegCapabilities =
                    await _hardwareCapabilitiesFactory.GetFFmpegCapabilities(ffmpegPath.Value, cancellationToken);
                aviSynthDemuxer = ffmpegCapabilities.HasDemuxFormat(FFmpegKnownFormat.AviSynth);
                aviSynthInstalled = _hardwareCapabilitiesFactory.IsAviSynthInstalled();
            }
        }

        var environment = new Dictionary<string, string>();
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            if (de is { Key: string key, Value: string value })
            {
                if (key.StartsWith("ETV_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("PROVIDER", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ELASTICSEARCH", StringComparison.OrdinalIgnoreCase))
                {
                    // ELASTICSEARCH__URI (ElasticSearch:Uri) may carry basic auth credentials
                    if (key.StartsWith("ELASTICSEARCH", StringComparison.OrdinalIgnoreCase)
                        && Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                        && !string.IsNullOrEmpty(uri.UserInfo))
                    {
                        environment[key] = new UriBuilder(uri) { UserName = null, Password = null }.Uri.ToString();
                    }
                    else
                    {
                        environment[key] = value;
                    }
                }
            }
        }

        List<CpuModel> cpuList = _hardwareCapabilitiesFactory.GetCpuList();
        List<VideoControllerModel> videoControllerList = _hardwareCapabilitiesFactory.GetVideoControllerList();

        return new TroubleshootingInfo(
            version,
            request.NextVersion,
            ffmpegVersion,
            environment,
            cpuList,
            videoControllerList,
            healthCheckSummaries,
            ffmpegSettings,
            ffmpegProfiles,
            channels,
            channelWatermarks,
            aviSynthDemuxer,
            aviSynthInstalled,
            nvidiaCapabilities,
            qsvCapabilities.ToString(),
            vaapiCapabilities.ToString(),
            videoToolboxCapabilities.ToString());
    }

    private static string GetDriverName(VaapiDriver driver)
    {
        switch (driver)
        {
            case VaapiDriver.i965:
                return "i965";
            case VaapiDriver.iHD:
                return "iHD";
            case VaapiDriver.RadeonSI:
                return "radeonsi";
            case VaapiDriver.Nouveau:
                return "nouveau";
        }

        return null;
    }
}
