using System.Diagnostics;
using CliWrap;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Images;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Iptv;
using ErsatzTV.Extensions;
using ErsatzTV.FFmpeg;
using ErsatzTV.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter(typeof(ConditionalIptvAuthorizeFilter))]
public class IptvController : StreamingControllerBase
{
    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly ILogger<IptvController> _logger;
    private readonly IMediator _mediator;

    public IptvController(
        IMediator mediator,
        IGraphicsEngine graphicsEngine,
        ILogger<IptvController> logger,
        IFFmpegSegmenterService ffmpegSegmenterService)
        : base(graphicsEngine, logger)
    {
        _mediator = mediator;
        _logger = logger;
        _ffmpegSegmenterService = ffmpegSegmenterService;
    }

    [HttpHead("iptv/channels.m3u")]
    [HttpGet("iptv/channels.m3u")]
    public Task<IActionResult> GetChannelPlaylist(
        [FromQuery]
        string mode = "mixed") =>
        _mediator.Send(
                new GetChannelPlaylist(
                    Request.Scheme,
                    Request.Host.ToString(),
                    Request.PathBase,
                    mode,
                    Request.Headers.UserAgent,
                    Request.Query["access_token"]))
            .Map<ChannelPlaylist, IActionResult>(Ok);

    [HttpHead("iptv/xmltv.xml")]
    [HttpGet("iptv/xmltv.xml")]
    public Task<IActionResult> GetGuide() =>
        _mediator.Send(
                new GetChannelGuide(
                    Request.Scheme,
                    Request.Host.ToString(),
                    Request.PathBase,
                    Request.Query["access_token"]))
            .ToActionResult();

    [HttpHead("iptv/hdhr/channel/{channelNumber}.ts")]
    [HttpGet("iptv/hdhr/channel/{channelNumber}.ts")]
    public Task<IActionResult> GetHDHRVideo(string channelNumber, [FromQuery] string mode = "ts")
    {
        // don't redirect to the correct channel mode for HDHR clients; always use TS
        if (mode != "ts" && mode != "ts-legacy")
        {
            mode = "ts";
        }

        return GetTransportStreamVideo(channelNumber, mode);
    }

    [HttpHead("iptv/channel/{channelNumber}.ts")]
    [HttpGet("iptv/channel/{channelNumber}.ts")]
    public async Task<IActionResult> GetTransportStreamVideo(
        string channelNumber,
        [FromQuery]
        string mode = null)
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(new GetChannelByNumber(channelNumber));
        if (maybeChannel.IsNone || !await maybeChannel.Map(c => c.IsEnabled).IfNoneAsync(false))
        {
            return NotFound();
        }

        // if mode is "unspecified" - find the configured mode and set it or redirect
        if (string.IsNullOrWhiteSpace(mode) || mode == "mixed")
        {
            foreach (ChannelViewModel channel in maybeChannel)
            {
                switch (channel.StreamingMode)
                {
                    case StreamingMode.TransportStream:
                        mode = "ts-legacy";
                        break;
                    case StreamingMode.TransportStreamHybrid:
                        mode = "ts";
                        break;
                    default:
                        return Redirect($"~/iptv/channel/{channelNumber}.m3u8{AccessTokenQuery()}");
                }
            }
        }

        FFmpegProcessRequest request = mode switch
        {
            "ts-legacy" => new GetConcatProcessByChannelNumber(Request.Scheme, Request.Host.ToString(), channelNumber),
            _ => new GetWrappedProcessByChannelNumber(
                Request.Scheme,
                Request.Host.ToString(),
                Request.Query["access_token"],
                channelNumber)
        };

        return await _mediator.Send(request)
            .Map(result => result.Match<IActionResult>(
                processModel =>
                {
                    Command command = processModel.Process;

                    _logger.LogInformation("Starting ts stream for channel {ChannelNumber}", channelNumber);
                    _logger.LogDebug("ffmpeg arguments {FFmpegArguments}", command.Arguments);
                    var process = new FFmpegProcess
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = command.TargetFilePath,
                            Arguments = command.Arguments,
                            RedirectStandardOutput = true,
                            RedirectStandardError = false,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    HttpContext.Response.RegisterForDispose(process);

                    foreach ((string key, string value) in command.EnvironmentVariables)
                    {
                        process.StartInfo.Environment[key] = value;
                    }

                    process.Start();
                    return new FileStreamResult(process.StandardOutput.BaseStream, "video/mp2t");
                },
                error => BadRequest(error.Value)));
    }

    [HttpHead("iptv/session/{channelNumber}/hls.m3u8")]
    [HttpGet("iptv/session/{channelNumber}/hls.m3u8")]
    public async Task<IActionResult> GetLivePlaylist(string channelNumber, CancellationToken cancellationToken)
    {
        // _logger.LogDebug("Checking for session worker for channel {Channel}", channelNumber);

        if (_ffmpegSegmenterService.TryGetWorker(channelNumber, out IHlsSessionWorker worker) && worker is not null)
        {
            // _logger.LogDebug("Trimming playlist for channel {Channel}", channelNumber);

            DateTimeOffset now = DateTimeOffset.Now.AddSeconds(-30);
            Option<TrimPlaylistResult> maybePlaylist = await worker.TrimPlaylist(now, cancellationToken);
            foreach (TrimPlaylistResult result in maybePlaylist)
            {
                return Content(result.Playlist, "application/vnd.apple.mpegurl");
            }

            // the session worker exists, so this is transient: either ffmpeg hasn't written the
            // first playlist yet, or the read failed. 404 tells clients the stream is gone and
            // many will abandon it, so ask them to retry shortly instead.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Trim playlist was canceled for channel {Channel}", channelNumber);
            }
            else
            {
                _logger.LogWarning(
                    "Trim playlist failure for channel {Channel}; will ask client to retry",
                    channelNumber);
            }

            Response.Headers.RetryAfter = "1";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        _logger.LogWarning(
            "Unable to locate session worker for channel {Channel}; will redirect to start session",
            channelNumber);
        return RedirectToAction(nameof(GetHttpLiveStreamingVideo), new { channelNumber });
    }

    [HttpHead("iptv/channel/{channelNumber}.m3u8")]
    [HttpGet("iptv/channel/{channelNumber}.m3u8")]
    public async Task<IActionResult> GetHttpLiveStreamingVideo(
        string channelNumber,
        [FromQuery]
        string mode = "mixed")
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(new GetChannelByNumber(channelNumber));
        if (maybeChannel.IsNone || !await maybeChannel.Map(c => c.IsEnabled).IfNoneAsync(false))
        {
            return NotFound();
        }

        // if mode is "unspecified" - find the configured mode and set it or redirect
        if (string.IsNullOrWhiteSpace(mode) || mode == "mixed")
        {
            foreach (ChannelViewModel channel in maybeChannel)
            {
                switch (channel.StreamingMode)
                {
                    case StreamingMode.HttpLiveStreamingDirect:
                        mode = "hls-direct";
                        break;
                    case StreamingMode.HttpLiveStreamingSegmenter:
                        mode = "segmenter";
                        break;
                    default:
                        return Redirect($"~/iptv/channel/{channelNumber}.ts{AccessTokenQuery()}");
                }
            }
        }

        switch (mode)
        {
            case "segmenter":
            case "segmenter-v2":
            case "segmenter-fmp4":
                _logger.LogDebug(
                    "Maybe starting ffmpeg session for channel {Channel}, mode {Mode}",
                    channelNumber,
                    mode);
                StreamingEngine streamingEngine =
                    await maybeChannel.Map(c => c.StreamingEngine).IfNoneAsync(StreamingEngine.Legacy);
                IRequest<Either<BaseError, string>> request = streamingEngine is StreamingEngine.Legacy
                    ? new StartFFmpegSession(
                        channelNumber,
                        mode,
                        Request.Scheme,
                        Request.Host.ToString(),
                        Request.PathBase,
                        AccessTokenQuery())
                    : new StartFFmpegNextSession(
                        channelNumber,
                        mode,
                        Request.Scheme,
                        Request.Host.ToString(),
                        Request.PathBase,
                        AccessTokenQuery());
                Either<BaseError, string> result = await _mediator.Send(request);
                return result.Match<IActionResult>(
                    multiVariantPlaylist =>
                    {
                        _logger.LogDebug(
                            "Session started; returning multi-variant playlist for channel {Channel}",
                            channelNumber);

                        return Content(multiVariantPlaylist, "application/vnd.apple.mpegurl");
                        // return Redirect($"~/iptv/session/{channelNumber}/hls.m3u8");
                    },
                    error =>
                    {
                        switch (error)
                        {
                            case ChannelSessionAlreadyActive active:
                                _logger.LogDebug(
                                    "Session is already active; returning multi-variant playlist for channel {Channel}",
                                    channelNumber);

                                return Content(active.MultiVariantPlaylist, "application/vnd.apple.mpegurl");
                            // return RedirectPreserveMethod($"iptv/session/{channelNumber}/hls.m3u8");
                            default:
                                _logger.LogWarning(
                                    "Failed to start segmenter for channel {ChannelNumber}: {Error}",
                                    channelNumber,
                                    error.ToString());
                                return NotFound();
                        }
                    });
            default:
                return await _mediator.Send(
                        new GetHlsPlaylistByChannelNumber(
                            Request.Scheme,
                            Request.Host.ToString(),
                            channelNumber,
                            mode,
                            Request.Query["access_token"]))
                    .Map(r => r.Match<IActionResult>(
                        playlist => Content(playlist, "application/vnd.apple.mpegurl"),
                        error => BadRequest(error.Value)));
        }
    }

    [HttpHead("iptv/logos/{fileName}")]
    [HttpGet("iptv/logos/{fileName}")]
    [HttpHead("iptv/logos/{fileName}.jpg")]
    [HttpGet("iptv/logos/{fileName}.jpg")]
    public async Task<IActionResult> GetImage(string fileName, [FromQuery] string contentType)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await _mediator.Send(new GetCachedImagePath(fileName, ArtworkKind.Logo, contentType));
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    [HttpGet("iptv/hls-direct/{channelNumber}")]
    [HttpGet("iptv/hls-direct/{channelNumber}.ts")]
    [HttpGet("iptv/hls-direct/{channelNumber}.mkv")]
    [HttpGet("iptv/hls-direct/{channelNumber}.mp4")]
    public async Task<IActionResult> GetStream(string channelNumber) =>
        await GetHlsDirectStream(channelNumber);

    private async Task<IActionResult> GetHlsDirectStream(string channelNumber)
    {
        var request = new GetPlayoutItemProcessByChannelNumber(
            channelNumber,
            StreamingMode.HttpLiveStreamingDirect,
            DateTimeOffset.Now,
            false,
            true,
            DateTimeOffset.Now,
            TimeSpan.Zero,
            Option<FrameRate>.None,
            IsTroubleshooting: false,
            Option<int>.None);

        Either<BaseError, PlayoutItemProcessModel> result = await _mediator.Send(request);

        return GetProcessResponse(result, channelNumber, StreamingMode.HttpLiveStreamingDirect);
    }

    private string AccessTokenQuery() => string.IsNullOrWhiteSpace(Request.Query["access_token"])
        ? string.Empty
        : $"?access_token={Request.Query["access_token"]}";
}
