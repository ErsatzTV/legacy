using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.FFmpeg;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.FFmpegProfiles;

public class UpdateFFmpegSettingsHandler(
    IConfigElementRepository configElementRepository,
    IFileSystem fileSystem,
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel)
    : IRequestHandler<UpdateFFmpegSettings, Either<BaseError, Unit>>
{
    public Task<Either<BaseError, Unit>> Handle(
        UpdateFFmpegSettings request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(_ => ApplyUpdate(request, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Validation<BaseError, Unit>> Validate(UpdateFFmpegSettings request) =>
        (await FFmpegMustExist(request), await FFprobeMustExist(request))
        .Apply((_, _) => Unit.Default);

    private Task<Validation<BaseError, Unit>> FFmpegMustExist(UpdateFFmpegSettings request) =>
        ValidateToolPath(request.Settings.FFmpegPath, "ffmpeg");

    private Task<Validation<BaseError, Unit>> FFprobeMustExist(UpdateFFmpegSettings request) =>
        ValidateToolPath(request.Settings.FFprobePath, "ffprobe");

    private async Task<Validation<BaseError, Unit>> ValidateToolPath(string path, string name)
    {
        if (!fileSystem.File.Exists(path))
        {
            return BaseError.New($"{name} path does not exist");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var test = new Process();
        test.StartInfo = startInfo;

        test.Start();
        string output = await test.StandardOutput.ReadToEndAsync();
        await test.WaitForExitAsync();
        return test.ExitCode == 0 && output.Contains($"{name} version")
            ? Unit.Default
            : BaseError.New($"Unable to verify {name} version");
    }

    private async Task<Unit> ApplyUpdate(UpdateFFmpegSettings request, CancellationToken cancellationToken)
    {
        await configElementRepository.Upsert(ConfigElementKey.FFmpegPath, request.Settings.FFmpegPath, cancellationToken);
        await configElementRepository.Upsert(ConfigElementKey.FFprobePath, request.Settings.FFprobePath, cancellationToken);
        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegDefaultProfileId,
            request.Settings.DefaultFFmpegProfileId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegSaveReports,
            request.Settings.SaveReports.ToString(),
            cancellationToken);
        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegHlsDirectOutputFormat,
            request.Settings.HlsDirectOutputFormat,
            cancellationToken);
        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegDefaultMpegTsScript,
            request.Settings.DefaultMpegTsScript,
            cancellationToken);

        if (request.Settings.SaveReports && !Directory.Exists(FileSystemLayout.FFmpegReportsFolder))
        {
            Directory.CreateDirectory(FileSystemLayout.FFmpegReportsFolder);
        }

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegPreferredLanguageCode,
            request.Settings.PreferredAudioLanguageCode,
            cancellationToken);

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegUseEmbeddedSubtitles,
            request.Settings.UseEmbeddedSubtitles,
            cancellationToken);

        // do not extract when subtitles are not used
        if (!request.Settings.UseEmbeddedSubtitles)
        {
            request.Settings.ExtractEmbeddedSubtitles = false;
        }

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegExtractEmbeddedSubtitles,
            request.Settings.ExtractEmbeddedSubtitles,
            cancellationToken);

        // queue extracting all embedded subtitles
        if (request.Settings.ExtractEmbeddedSubtitles)
        {
            await workerChannel.WriteAsync(new ExtractEmbeddedSubtitles(Option<int>.None), cancellationToken);
        }

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegProbeForInterlacedFrames,
            request.Settings.ProbeForInterlacedFrames,
            cancellationToken);

        Option<int> maybeOldGlobalWatermarkId = await configElementRepository.GetValue<int>(
            ConfigElementKey.FFmpegGlobalWatermarkId,
            cancellationToken);

        if (request.Settings.GlobalWatermarkId is not null)
        {
            await configElementRepository.Upsert(
                ConfigElementKey.FFmpegGlobalWatermarkId,
                request.Settings.GlobalWatermarkId.Value,
                cancellationToken);
        }
        else
        {
            await configElementRepository.Delete(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken);
        }

        if (maybeOldGlobalWatermarkId != Optional(request.Settings.GlobalWatermarkId))
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            List<string> channelNumbers = await NextPlayoutDependents.All(dbContext, cancellationToken);
            await workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);
        }

        if (request.Settings.GlobalFallbackFillerId is not null)
        {
            await configElementRepository.Upsert(
                ConfigElementKey.FFmpegGlobalFallbackFillerId,
                request.Settings.GlobalFallbackFillerId.Value,
                cancellationToken);
        }
        else
        {
            await configElementRepository.Delete(ConfigElementKey.FFmpegGlobalFallbackFillerId, cancellationToken);
        }

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegSegmenterTimeout,
            request.Settings.HlsSegmenterIdleTimeout,
            cancellationToken);

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegWorkAheadSegmenters,
            request.Settings.WorkAheadSegmenterLimit,
            cancellationToken);

        await configElementRepository.Upsert(
            ConfigElementKey.FFmpegInitialSegmentCount,
            request.Settings.InitialSegmentCount,
            cancellationToken);

        await workerChannel.WriteAsync(new RefreshFFmpegCapabilities(), cancellationToken);

        return Unit.Default;
    }
}
