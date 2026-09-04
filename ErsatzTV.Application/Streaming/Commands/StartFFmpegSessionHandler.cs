using System.Globalization;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.OutputFormat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class StartFFmpegSessionHandler : IRequestHandler<StartFFmpegSession, Either<BaseError, string>>
{
    private static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly IGraphicsEngine _graphicsEngine;
    private readonly IHlsPlaylistFilter _hlsPlaylistFilter;
    private readonly IHlsInitSegmentCache _hlsInitSegmentCache;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILogger<StartFFmpegSessionHandler> _logger;
    private readonly IMediator _mediator;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<HlsSessionWorker> _sessionWorkerLogger;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public StartFFmpegSessionHandler(
        IHlsPlaylistFilter hlsPlaylistFilter,
        IHlsInitSegmentCache hlsInitSegmentCache,
        IServiceScopeFactory serviceScopeFactory,
        IMediator mediator,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILogger<StartFFmpegSessionHandler> logger,
        ILogger<HlsSessionWorker> sessionWorkerLogger,
        IFFmpegSegmenterService ffmpegSegmenterService,
        IConfigElementRepository configElementRepository,
        IGraphicsEngine graphicsEngine,
        IHostApplicationLifetime hostApplicationLifetime,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _hlsPlaylistFilter = hlsPlaylistFilter;
        _hlsInitSegmentCache = hlsInitSegmentCache;
        _serviceScopeFactory = serviceScopeFactory;
        _mediator = mediator;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _logger = logger;
        _sessionWorkerLogger = sessionWorkerLogger;
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _configElementRepository = configElementRepository;
        _graphicsEngine = graphicsEngine;
        _hostApplicationLifetime = hostApplicationLifetime;
        _workerChannel = workerChannel;
    }

    public async Task<Either<BaseError, string>> Handle(StartFFmpegSession request, CancellationToken cancellationToken)
    {
        using IDisposable releaser =
            await _ffmpegSegmenterService.LockForStart(request.ChannelNumber, cancellationToken);

        if (_ffmpegSegmenterService.TryGetWorker(request.ChannelNumber, out IHlsSessionWorker existing))
        {
            existing.Touch(Option<string>.None);
            return new ChannelSessionAlreadyActive(await GetMultiVariantPlaylist(request));
        }

        Option<TimeSpan> idleTimeout = await _configElementRepository
            .GetValue<int>(ConfigElementKey.FFmpegSegmenterTimeout, cancellationToken)
            .Map(maybeTimeout => maybeTimeout.Match(i => TimeSpan.FromSeconds(i), () => TimeSpan.FromMinutes(1)));

        Option<FrameRate> targetFramerate = await _mediator.Send(
            new GetChannelFramerate(request.ChannelNumber),
            cancellationToken);

        int initialSegmentCount = await _configElementRepository
            .GetValue<int>(ConfigElementKey.FFmpegInitialSegmentCount, cancellationToken)
            .Map(maybeCount => maybeCount.Match(identity, () => 1));

        // disable idle timeout when configured to keep running
        Option<ChannelViewModel> channel =
            await _mediator.Send(new GetChannelByNumber(request.ChannelNumber), cancellationToken);
        if (await channel.Map(c => c.IdleBehavior is ChannelIdleBehavior.KeepRunning).IfNoneAsync(false))
        {
            idleTimeout = Option<TimeSpan>.None;
        }

        await _mediator.Send(new RefreshGraphicsElements(), cancellationToken);

        PrepareTranscodeFolder(request.ChannelNumber);

        HlsSessionWorker worker = GetSessionWorker(request, targetFramerate);
        if (!_ffmpegSegmenterService.TryAddWorker(request.ChannelNumber, worker))
        {
            return new ChannelSessionAlreadyActive(await GetMultiVariantPlaylist(request));
        }

        // fire and forget worker
        Task runTask = worker.Run(request.ChannelNumber, idleTimeout, _hostApplicationLifetime.ApplicationStopping);
        _ = runTask.ContinueWith(
            _ =>
            {
                _ffmpegSegmenterService.RemoveWorker(request.ChannelNumber, worker);

                ((IDisposable)worker).Dispose();

                _workerChannel.TryWrite(new ReleaseMemory(false));
            },
            TaskScheduler.Default);

        Either<BaseError, Unit> ready = await SessionStartWait.ForReady(
            request.ChannelNumber,
            worker,
            runTask,
            initialSegmentCount,
            StartDeadline,
            cancellationToken);
        return await ready.MapAsync(async _ => await GetMultiVariantPlaylist(request));
    }

    private HlsSessionWorker GetSessionWorker(StartFFmpegSession request, Option<FrameRate> targetFramerate) =>
        request.Mode switch
        {
            _ => new HlsSessionWorker(
                _serviceScopeFactory,
                _graphicsEngine,
                OutputFormatKind.Hls,
                _hlsPlaylistFilter,
                _hlsInitSegmentCache,
                _configElementRepository,
                _fileSystem,
                _localFileSystem,
                _sessionWorkerLogger,
                targetFramerate)
        };

    private void PrepareTranscodeFolder(string channelNumber)
    {
        string folder = Path.Combine(FileSystemLayout.TranscodeFolder, channelNumber);
        _logger.LogDebug("Preparing transcode folder {Folder}", folder);

        _localFileSystem.EnsureFolderExists(folder);
        _localFileSystem.EmptyFolder(folder);
    }

    private async Task<string> GetMultiVariantPlaylist(StartFFmpegSession request)
    {
        var variantPlaylist =
            $"{request.Scheme}://{request.Host}{request.PathBase}/iptv/session/{request.ChannelNumber}/hls.m3u8{request.AccessTokenQuery}";

        Option<ChannelStreamingSpecsViewModel> maybeStreamingSpecs =
            await _mediator.Send(new GetChannelStreamingSpecs(request.ChannelNumber));
        string resolution = string.Empty;
        var bitrate = "10000000";
        foreach (ChannelStreamingSpecsViewModel streamingSpecs in maybeStreamingSpecs)
        {
            string videoCodec = streamingSpecs.VideoFormat switch
            {
                FFmpegProfileVideoFormat.Av1 => "av01.0.01M.08",
                FFmpegProfileVideoFormat.Hevc => "hvc1.1.6.L93.B0",
                FFmpegProfileVideoFormat.H264 => "avc1.4D4028",
                _ => string.Empty
            };

            string audioCodec = streamingSpecs.AudioFormat switch
            {
                FFmpegProfileAudioFormat.Ac3 => "ac-3",
                FFmpegProfileAudioFormat.Aac or FFmpegProfileAudioFormat.AacLatm => "mp4a.40.2",
                _ => string.Empty
            };

            List<string> codecStrings = [];
            if (!string.IsNullOrWhiteSpace(videoCodec))
            {
                codecStrings.Add(videoCodec);
            }

            if (!string.IsNullOrWhiteSpace(audioCodec))
            {
                codecStrings.Add(audioCodec);
            }

            string codecs = codecStrings.Count > 0 ? $",CODECS=\"{string.Join(",", codecStrings)}\"" : string.Empty;
            resolution = $",RESOLUTION={streamingSpecs.Width}x{streamingSpecs.Height}{codecs}";
            bitrate = streamingSpecs.Bitrate.ToString(CultureInfo.InvariantCulture);
        }

        return $@"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-STREAM-INF:BANDWIDTH={bitrate}{resolution}
{variantPlaylist}";
    }
}
