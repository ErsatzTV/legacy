using System.Collections;
using System.Reflection;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Troubleshooting.Queries;

public class GetTroubleshootingInfoHandler : IRequestHandler<GetTroubleshootingInfo, TroubleshootingInfo>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IHardwareCapabilitiesFactory _hardwareCapabilitiesFactory;
    private readonly IHealthCheckService _healthCheckService;
    private readonly IMediator _mediator;
    private readonly INextProbeService _nextProbeService;

    public GetTroubleshootingInfoHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IHealthCheckService healthCheckService,
        IHardwareCapabilitiesFactory hardwareCapabilitiesFactory,
        IConfigElementRepository configElementRepository,
        INextProbeService nextProbeService,
        IMediator mediator)
    {
        _dbContextFactory = dbContextFactory;
        _healthCheckService = healthCheckService;
        _hardwareCapabilitiesFactory = hardwareCapabilitiesFactory;
        _configElementRepository = configElementRepository;
        _nextProbeService = nextProbeService;
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

        Option<ConfigElement> maybeFFmpegPath =
            await _configElementRepository.GetConfigElement(ConfigElementKey.FFmpegPath, cancellationToken);
        foreach (ConfigElement ffmpegPath in maybeFFmpegPath)
        {
            ffmpegVersion = await _hardwareCapabilitiesFactory.GetFFmpegVersion(ffmpegPath.Value, cancellationToken);

            var ffmpegCapabilities =
                await _hardwareCapabilitiesFactory.GetFFmpegCapabilities(ffmpegPath.Value, cancellationToken);
            aviSynthDemuxer = ffmpegCapabilities.HasDemuxFormat(FFmpegKnownFormat.AviSynth);
            aviSynthInstalled = _hardwareCapabilitiesFactory.IsAviSynthInstalled();
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

        Option<Describe> nextDescribe = await _mediator.Send(new GetNextDescribe(), cancellationToken);

        return new TroubleshootingInfo(
            version,
            request.NextVersion,
            nextDescribe.MatchUnsafe(identity, () => null),
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
            _nextProbeService.Latest.MatchUnsafe(identity, () => null));
    }
}
