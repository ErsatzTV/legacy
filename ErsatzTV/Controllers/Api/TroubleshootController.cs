using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application;
using ErsatzTV.Application.MediaItems;
using ErsatzTV.Application.Troubleshooting;
using ErsatzTV.Application.Troubleshooting.Queries;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace ErsatzTV.Controllers.Api;

[ApiController]
public class TroubleshootController(
    ChannelWriter<IFFmpegWorkerRequest> channelWriter,
    IFileSystem fileSystem,
    IConfigElementRepository configElementRepository,
    ITroubleshootingNotifier notifier,
    IMediator mediator) : ControllerBase
{
    [HttpHead("api/troubleshoot/playback.m3u8")]
    [HttpGet("api/troubleshoot/playback.m3u8")]
    public async Task<IActionResult> TroubleshootPlayback(
        [FromQuery]
        int mediaItem,
        [FromQuery]
        int channel,
        [FromQuery]
        int ffmpegProfile,
        [FromQuery]
        StreamingMode streamingMode,
        [FromQuery]
        List<int> watermark,
        [FromQuery]
        List<int> graphicsElement,
        [FromQuery]
        string streamSelector,
        [FromQuery]
        int? subtitleId,
        [FromQuery]
        int seekSeconds,
        [FromQuery]
        DateTimeOffset? start,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        using var logContext = LogContext.PushProperty(InMemoryLogService.CorrelationIdKey, sessionId);

        try
        {
            Option<int> ss = seekSeconds > 0 ? seekSeconds : Option<int>.None;

            Either<BaseError, PlayoutItemResult> result = await mediator.Send(
                new PrepareTroubleshootingPlayback(
                    sessionId,
                    streamingMode,
                    mediaItem,
                    channel,
                    ffmpegProfile,
                    streamSelector,
                    watermark,
                    graphicsElement,
                    subtitleId,
                    ss,
                    Optional(start)),
                cancellationToken);

            if (result.IsLeft)
            {
                return NotFound();
            }

            foreach (PlayoutItemResult playoutItemResult in result.RightToSeq())
            {
                Either<BaseError, MediaItemInfo> maybeMediaInfo =
                    await mediator.Send(
                        new GetMediaItemInfo(await playoutItemResult.MediaItemId.IfNoneAsync(0)),
                        cancellationToken);

                try
                {
                    TroubleshootingInfo troubleshootingInfo = await mediator.Send(
                        new GetTroubleshootingInfo(NextVersion.Version),
                        cancellationToken);

                    // filter ffmpeg profiles
                    troubleshootingInfo.FFmpegProfiles.RemoveAll(p => p.Id != ffmpegProfile);

                    // filter watermarks
                    troubleshootingInfo.Watermarks.RemoveAll(p => !watermark.Contains(p.Id));

                    await channelWriter.WriteAsync(
                        new StartTroubleshootingPlayback(
                            sessionId,
                            streamSelector,
                            playoutItemResult,
                            maybeMediaInfo.ToOption(),
                            troubleshootingInfo),
                        cancellationToken);

                    string playlistFile = Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "live.m3u8");
                    while (!fileSystem.File.Exists(playlistFile))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                        if (cancellationToken.IsCancellationRequested || notifier.IsFailed(sessionId))
                        {
                            break;
                        }
                    }

                    int initialSegmentCount = await configElementRepository
                        .GetValue<int>(ConfigElementKey.FFmpegInitialSegmentCount, cancellationToken)
                        .Map(maybeCount => maybeCount.Match(c => c, () => 1));

                    initialSegmentCount = Math.Max(initialSegmentCount, 2);

                    bool hasSegments = false;
                    while (!hasSegments)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

                        string[] segmentFiles = streamingMode switch
                        {
                            // StreamingMode.HttpLiveStreamingSegmenter => Directory.GetFiles(
                            //     FileSystemLayout.TranscodeTroubleshootingFolder,
                            //     "*.m4s"),
                            _ => Directory.GetFiles(FileSystemLayout.TranscodeTroubleshootingFolder, "*.ts")
                        };

                        if (segmentFiles.Length >= initialSegmentCount)
                        {
                            hasSegments = true;
                        }
                    }

                    if (!notifier.IsFailed(sessionId))
                    {
                        return Redirect("~/iptv/session/.troubleshooting/live.m3u8");
                    }
                }
                finally
                {
                    notifier.RemoveSession(sessionId);
                }
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return NotFound();
    }

    [HttpHead("api/troubleshoot/playback/archive")]
    [HttpGet("api/troubleshoot/playback/archive")]
    public async Task<IActionResult> TroubleshootPlaybackArchive(CancellationToken cancellationToken)
    {
        Option<string> maybeArchivePath = await mediator.Send(new ArchiveTroubleshootingResults(), cancellationToken);
        foreach (string archivePath in maybeArchivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.DeleteOnClose);
            return File(
                fs,
                "application/zip",
                $"ersatztv-troubleshooting-{DateTimeOffset.Now.ToUnixTimeSeconds()}.zip");
        }

        return NotFound();
    }

    [HttpHead("api/troubleshoot/playback/sample/{mediaItemId:int}")]
    [HttpGet("api/troubleshoot/playback/sample/{mediaItemId:int}")]
    public async Task<IActionResult> TroubleshootPlaybackSample(int mediaItemId, CancellationToken cancellationToken)
    {
        Option<string> maybeArchivePath = await mediator.Send(new ArchiveMediaSample(mediaItemId), cancellationToken);
        foreach (string archivePath in maybeArchivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.DeleteOnClose);
            return File(
                fs,
                "application/zip",
                $"ersatztv-media-sample-{DateTimeOffset.Now.ToUnixTimeSeconds()}.zip");
        }

        return NotFound();
    }
}
