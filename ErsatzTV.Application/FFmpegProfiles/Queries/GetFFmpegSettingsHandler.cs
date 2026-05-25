using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.OutputFormat;

namespace ErsatzTV.Application.FFmpegProfiles;

public class GetFFmpegSettingsHandler(IConfigElementRepository configElementRepository)
    : IRequestHandler<GetFFmpegSettings, FFmpegSettingsViewModel>
{
    public async Task<FFmpegSettingsViewModel> Handle(
        GetFFmpegSettings request,
        CancellationToken cancellationToken)
    {
        Option<string> ffmpegPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);
        Option<string> ffprobePath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);
        Option<int> defaultFFmpegProfileId =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegDefaultProfileId, cancellationToken);
        Option<bool> saveReports =
            await configElementRepository.GetValue<bool>(ConfigElementKey.FFmpegSaveReports, cancellationToken);
        Option<string> preferredAudioLanguageCode =
            await configElementRepository.GetValue<string>(
                ConfigElementKey.FFmpegPreferredLanguageCode,
                cancellationToken);
        Option<bool> useEmbeddedSubtitles =
            await configElementRepository.GetValue<bool>(
                ConfigElementKey.FFmpegUseEmbeddedSubtitles,
                cancellationToken);
        Option<bool> extractEmbeddedSubtitles =
            await configElementRepository.GetValue<bool>(
                ConfigElementKey.FFmpegExtractEmbeddedSubtitles,
                cancellationToken);
        Option<bool> probeForInterlacedFrames =
            await configElementRepository.GetValue<bool>(
                ConfigElementKey.FFmpegProbeForInterlacedFrames,
                cancellationToken);
        Option<int> watermark =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken);
        Option<int> fallbackFiller =
            await configElementRepository.GetValue<int>(
                ConfigElementKey.FFmpegGlobalFallbackFillerId,
                cancellationToken);
        Option<int> hlsSegmenterIdleTimeout =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegSegmenterTimeout, cancellationToken);
        Option<int> workAheadSegmenterLimit =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegWorkAheadSegmenters, cancellationToken);
        Option<int> initialSegmentCount =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegInitialSegmentCount, cancellationToken);
        Option<OutputFormatKind> outputFormatKind =
            await configElementRepository.GetValue<OutputFormatKind>(
                ConfigElementKey.FFmpegHlsDirectOutputFormat,
                cancellationToken);
        Option<string> defaultMpegTsScript =
            await configElementRepository.GetValue<string>(
                ConfigElementKey.FFmpegDefaultMpegTsScript,
                cancellationToken);

        var result = new FFmpegSettingsViewModel
        {
            FFmpegPath = await ffmpegPath.IfNoneAsync(string.Empty),
            FFprobePath = await ffprobePath.IfNoneAsync(string.Empty),
            DefaultFFmpegProfileId = await defaultFFmpegProfileId.IfNoneAsync(0),
            SaveReports = await saveReports.IfNoneAsync(false),
            UseEmbeddedSubtitles = await useEmbeddedSubtitles.IfNoneAsync(true),
            ExtractEmbeddedSubtitles = await extractEmbeddedSubtitles.IfNoneAsync(false),
            ProbeForInterlacedFrames = await probeForInterlacedFrames.IfNoneAsync(false),
            PreferredAudioLanguageCode = await preferredAudioLanguageCode.IfNoneAsync("eng"),
            HlsSegmenterIdleTimeout = await hlsSegmenterIdleTimeout.IfNoneAsync(60),
            WorkAheadSegmenterLimit = await workAheadSegmenterLimit.IfNoneAsync(1),
            InitialSegmentCount = await initialSegmentCount.IfNoneAsync(1),
            HlsDirectOutputFormat = await outputFormatKind.IfNoneAsync(OutputFormatKind.MpegTs),
            DefaultMpegTsScript = await defaultMpegTsScript.IfNoneAsync("default"),
        };

        foreach (int watermarkId in watermark)
        {
            result.GlobalWatermarkId = watermarkId;
        }

        foreach (int fallbackFillerId in fallbackFiller)
        {
            result.GlobalFallbackFillerId = fallbackFillerId;
        }

        return result;
    }
}
