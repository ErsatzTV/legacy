using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.FFmpeg.Capabilities;

namespace ErsatzTV.Application.Troubleshooting;

public record TroubleshootingInfo(
    string Version,
    string NextVersion,
    Describe NextDescribe,
    string FFmpegVersion,
    Dictionary<string, string> Environment,
    List<CpuModel> Cpus,
    List<VideoControllerModel> VideoControllers,
    List<HealthCheckResultSummary> Health,
    FFmpegSettingsViewModel FFmpegSettings,
    List<FFmpegProfile> FFmpegProfiles,
    List<Channel> Channels,
    List<ChannelWatermark> Watermarks,
    bool AviSynthDemuxer,
    bool AviSynthInstalled,
    NextProbeResult NextProbe);
