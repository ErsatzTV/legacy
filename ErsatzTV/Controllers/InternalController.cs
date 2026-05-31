using System.CommandLine.Parsing;
using System.Diagnostics;
using CliWrap;
using ErsatzTV.Application.Emby;
using ErsatzTV.Application.Jellyfin;
using ErsatzTV.Application.MediaItems;
using ErsatzTV.Application.Plex;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Application.Subtitles.Queries;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Extensions;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Scheduling;
using Flurl;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace ErsatzTV.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class InternalController : StreamingControllerBase
{
    private readonly ILogger<InternalController> _logger;
    private readonly IMediator _mediator;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IDynamicPlayoutItemService _dynamicPlayoutItemService;
    private readonly IPlayoutItemConverter _playoutItemConverter;

    public InternalController(
        IGraphicsEngine graphicsEngine,
        IMediator mediator,
        IDbContextFactory<TvContext> dbContextFactory,
        IDynamicPlayoutItemService dynamicPlayoutItemService,
        IPlayoutItemConverter playoutItemConverter,
        ILogger<InternalController> logger)
        : base(graphicsEngine, logger)
    {
        _mediator = mediator;
        _dbContextFactory = dbContextFactory;
        _dynamicPlayoutItemService = dynamicPlayoutItemService;
        _playoutItemConverter = playoutItemConverter;
        _logger = logger;
    }

    [HttpGet("ffmpeg/concat/{channelNumber}")]
    public Task<IActionResult> GetConcatPlaylist(string channelNumber, [FromQuery] string mode = "ts-legacy") =>
        _mediator.Send(
                new GetConcatPlaylistByChannelNumber(Request.Scheme, Request.Host.ToString(), channelNumber, mode))
            .ToActionResult();

    [HttpGet("ffmpeg/stream/{channelNumber}")]
    public Task<IActionResult> GetStream(string channelNumber) => GetTsLegacyStream(channelNumber);

    [HttpGet("ffmpeg/music-video-credits/{playoutItemId:int}")]
    public async Task<IActionResult> GetMusicVideoCredits(
        int playoutItemId,
        [FromQuery] long? seekToMs,
        CancellationToken cancellationToken)
    {
        Option<string> maybeCreditsFile = await _mediator.Send(
            new GetMusicVideoCreditsByPlayoutItemId(playoutItemId, Optional(seekToMs)),
            cancellationToken);
        foreach (string creditsFile in maybeCreditsFile)
        {
            return new PhysicalFileResult(creditsFile, "text/x-ssa");
        }

        return NotFound();
    }

    [HttpGet("ffmpeg/remote-stream/{remoteStreamId}")]
    public async Task<IActionResult> GetRemoteStream(int remoteStreamId, CancellationToken cancellationToken)
    {
        Option<RemoteStreamViewModel> maybeRemoteStream =
            await _mediator.Send(new GetRemoteStreamById(remoteStreamId), cancellationToken);

        foreach (RemoteStreamViewModel remoteStream in maybeRemoteStream)
        {
            if (!string.IsNullOrWhiteSpace(remoteStream.Url))
            {
                return new RedirectResult(remoteStream.Url);
            }

            if (!string.IsNullOrWhiteSpace(remoteStream.Script))
            {
                var split = CommandLineParser.SplitCommandLine(remoteStream.Script).ToList();
                if (split.Count > 0)
                {
                    _logger.LogDebug("Remote stream script: {Arguments}", split);

                    Command command = Cli.Wrap(split.Head());
                    if (split.Count > 1)
                    {
                        command = command.WithArguments(split.Tail());
                    }

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
                }
            }
        }

        return NotFound();
    }

    [HttpGet("/media/plex/{plexMediaSourceId:int}/{*path}")]
    public async Task<IActionResult> GetPlexMedia(
        int plexMediaSourceId,
        string path,
        CancellationToken cancellationToken)
    {
#if DEBUG_NO_SYNC
        await Task.Delay(100, cancellationToken);
        return NotFound();
#else
        Either<BaseError, PlexConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetPlexConnectionParameters(plexMediaSourceId), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath = new Uri(r.Uri, path).SetQueryParam("X-Plex-Token", r.AuthToken);
                return new RedirectResult(fullPath.ToString());
            });
#endif
    }

    [HttpGet("/media/jellyfin/{*path}")]
    public async Task<IActionResult> GetJellyfinMedia(string path, CancellationToken cancellationToken)
    {
        Either<BaseError, JellyfinConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetJellyfinConnectionParameters(), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath;

                if (path.Contains("Subtitles"))
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment(path);
                }
                else
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment("Videos")
                        .AppendPathSegment(path)
                        .AppendPathSegment("stream")
                        .SetQueryParam("static", "true");
                }

                return new RedirectResult(fullPath.ToString());
            });
    }

    [HttpGet("/media/emby/{*path}")]
    public async Task<IActionResult> GetEmbyMedia(string path, CancellationToken cancellationToken)
    {
        Either<BaseError, EmbyConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetEmbyConnectionParameters(), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath;

                if (path.Contains("Subtitles"))
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment(path)
                        .SetQueryParam("X-Emby-Token", r.ApiKey);
                }
                else
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment("Videos")
                        .AppendPathSegment(path)
                        .AppendPathSegment("stream")
                        .SetQueryParam("static", "true")
                        .SetQueryParam("X-Emby-Token", r.ApiKey);
                }

                return new RedirectResult(fullPath.ToString());
            });
    }

    [HttpGet("/media/subtitle/{id:int}")]
    public async Task<IActionResult> GetSubtitle(int id, [FromQuery] long? seekToMs)
    {
        Either<BaseError, SubtitlePathAndCodec> maybePath = await _mediator.Send(new GetSubtitlePathById(id));

        foreach (SubtitlePathAndCodec pathAndCodec in maybePath.RightToSeq())
        {
            string mimeType = Path.GetExtension(pathAndCodec.Path ?? string.Empty).ToLowerInvariant() switch
            {
                ".ass" or ".ssa" => "text/x-ssa",
                ".vtt" => "text/vtt",
                _ when pathAndCodec.Codec.ToLowerInvariant() is "ass" or "ssa" => "text/x-ssa",
                _ when pathAndCodec.Codec.ToLowerInvariant() is "vtt" => "text/vtt",
                _ => "application/x-subrip"
            };

            if (seekToMs is > 0)
            {
                Either<BaseError, SeekTextSubtitleProcess> maybeProcess = await _mediator.Send(
                    new GetSeekTextSubtitleProcess(pathAndCodec, TimeSpan.FromMilliseconds(seekToMs.Value)));
                foreach (SeekTextSubtitleProcess processModel in maybeProcess.RightToSeq())
                {
                    Command command = processModel.Process;

                    _logger.LogDebug("ffmpeg text subtitle arguments {FFmpegArguments}", command.Arguments);

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
                    return new FileStreamResult(process.StandardOutput.BaseStream, mimeType);
                }

                return new NotFoundResult();
            }

            if (pathAndCodec.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return new RedirectResult(pathAndCodec.Path);
            }

            return new PhysicalFileResult(pathAndCodec.Path, mimeType);
        }

        return new NotFoundResult();
    }

    [HttpGet("/media/fallback")]
    public async Task<IActionResult> GetFallbackPlayoutJson(CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("x-etv-channel", out StringValues channelNumber) || channelNumber.Count != 1)
        {
            return BadRequest();
        }

        if (!Request.Headers.TryGetValue("x-etv-now", out StringValues nowString) || nowString.Count != 1 ||
            !DateTimeOffset.TryParse(nowString[0], out DateTimeOffset now))
        {
            return BadRequest();
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<Channel> maybeChannel = await dbContext.Channels
            .SingleOrDefaultAsync(c => c.Number == channelNumber[0], cancellationToken)
            .Map(Optional);

        foreach (var channel in maybeChannel)
        {
            Either<BaseError, PlayoutItemWithPath> maybePlayoutItem =
                await _dynamicPlayoutItemService.CheckForFallbackFiller(
                    dbContext,
                    channel,
                    now,
                    cancellationToken);

            foreach (var itemWithPath in maybePlayoutItem.RightToSeq())
            {
                Option<Core.Next.PlayoutItem> maybeNextPlayoutItem = await _playoutItemConverter.ToNext(
                    channelNumber[0],
                    itemWithPath.PlayoutItem,
                    cancellationToken);

                foreach (Core.Next.PlayoutItem nextPlayoutItem in maybeNextPlayoutItem)
                {
                    return Content(
                        JsonConvert.SerializeObject(nextPlayoutItem, Core.Next.Converter.Settings),
                        "application/json");
                }
            }

        }

        return NotFound();
    }

    private async Task<IActionResult> GetTsLegacyStream(string channelNumber)
    {
        var request = new GetPlayoutItemProcessByChannelNumber(
            channelNumber,
            StreamingMode.TransportStream,
            DateTimeOffset.Now,
            false,
            true,
            DateTimeOffset.Now,
            TimeSpan.Zero,
            Option<FrameRate>.None,
            IsTroubleshooting: false,
            Option<int>.None);

        Either<BaseError, PlayoutItemProcessModel> result = await _mediator.Send(request);

        return GetProcessResponse(result, channelNumber, StreamingMode.TransportStream);
    }
}
