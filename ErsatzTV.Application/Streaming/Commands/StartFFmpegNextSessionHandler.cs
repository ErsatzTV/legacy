using System.Globalization;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class StartFFmpegNextSessionHandler(
    IServiceScopeFactory serviceScopeFactory,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    IFFmpegSegmenterService ffmpegSegmenterService,
    IChannelConfigConverter channelConfigConverter,
    IConfigElementRepository configElementRepository,
    IHostApplicationLifetime hostApplicationLifetime,
    IMediator mediator,
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    ILogger<StartFFmpegNextSessionHandler> logger,
    ILogger<NextSessionWorker> sessionWorkerLogger)
    : NextChannelHandlerBase(fileSystem), IRequestHandler<StartFFmpegNextSession, Either<BaseError, string>>
{
    private static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<Either<BaseError, string>> Handle(
        StartFFmpegNextSession request,
        CancellationToken cancellationToken)
    {
        using IDisposable releaser =
            await ffmpegSegmenterService.LockForStart(request.ChannelNumber, cancellationToken);

        if (ffmpegSegmenterService.TryGetWorker(request.ChannelNumber, out IHlsSessionWorker existing))
        {
            existing.Touch(Option<string>.None);
            return new ChannelSessionAlreadyActive(await GetMultiVariantPlaylist(request));
        }

        Validation<BaseError, string> maybeChannelBinary = await ChannelBinaryMustExist();
        if (maybeChannelBinary.IsFail)
        {
            return maybeChannelBinary.FailToSeq().Head();
        }

        string channelBinary = maybeChannelBinary.SuccessToSeq().Head();

        Option<TimeSpan> idleTimeout = Option<TimeSpan>.None;

        // Option<FrameRate> targetFramerate = await mediator.Send(
        //     new GetChannelFramerate(request.ChannelNumber),
        //     cancellationToken);

        int initialSegmentCount = await configElementRepository
            .GetValue<int>(ConfigElementKey.FFmpegInitialSegmentCount, cancellationToken)
            .Map(maybeCount => maybeCount.Match(identity, () => 1));

        Option<ChannelViewModel> maybeChannel =
            await mediator.Send(new GetChannelByNumber(request.ChannelNumber), cancellationToken);

        if (maybeChannel.IsNone)
        {
            return BaseError.New($"Channel number {request.ChannelNumber} does not exist.");
        }

        ChannelViewModel channel = maybeChannel.Head();

        Option<FFmpegProfileViewModel> maybeFFmpegProfile = await mediator.Send(
            new GetFFmpegProfileById(channel.FFmpegProfileId),
            cancellationToken);

        if (maybeFFmpegProfile.IsNone)
        {
            return BaseError.New($"FFmpeg profile {channel.FFmpegProfileId} not exist");
        }

        FFmpegProfileViewModel ffmpegProfile = maybeFFmpegProfile.Head();

        // only load timeout when needed
        if (channel.IdleBehavior is not ChannelIdleBehavior.KeepRunning)
        {
            idleTimeout = await configElementRepository
                .GetValue<int>(ConfigElementKey.FFmpegSegmenterTimeout, cancellationToken)
                .Map(maybeTimeout => maybeTimeout.Match(i => TimeSpan.FromSeconds(i), () => TimeSpan.FromMinutes(1)));
        }

        await mediator.Send(new RefreshGraphicsElements(), cancellationToken);

        PrepareTranscodeFolder(request.ChannelNumber);

        ChannelConfig config = await channelConfigConverter.ToNext(channel, ffmpegProfile, cancellationToken);

        NextSessionWorker worker = new NextSessionWorker(
            channelBinary,
            config,
            _fileSystem,
            localFileSystem,
            serviceScopeFactory,
            sessionWorkerLogger);

        if (!ffmpegSegmenterService.TryAddWorker(request.ChannelNumber, worker))
        {
            return new ChannelSessionAlreadyActive(await GetMultiVariantPlaylist(request));
        }

        // fire and forget worker
        Task runTask = worker.Run(request.ChannelNumber, idleTimeout, hostApplicationLifetime.ApplicationStopping);
        _ = runTask.ContinueWith(
                _ =>
                {
                    ffmpegSegmenterService.RemoveWorker(request.ChannelNumber, worker);

                    ((IDisposable)worker).Dispose();

                    workerChannel.TryWrite(new ReleaseMemory(false));
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

    private void PrepareTranscodeFolder(string channelNumber)
    {
        string folder = Path.Combine(FileSystemLayout.TranscodeFolder, channelNumber);
        logger.LogDebug("Preparing transcode folder {Folder}", folder);

        localFileSystem.EnsureFolderExists(folder);
        localFileSystem.EmptyFolder(folder);
    }

    private async Task<string> GetMultiVariantPlaylist(StartFFmpegNextSession request)
    {
        var variantPlaylist =
            $"{request.Scheme}://{request.Host}{request.PathBase}/iptv/session/{request.ChannelNumber}/live.m3u8{request.AccessTokenQuery}";

        var subtitlePlaylist =
            $"{request.Scheme}://{request.Host}{request.PathBase}/iptv/session/{request.ChannelNumber}/live_sub.m3u8{request.AccessTokenQuery}";

        Option<ChannelStreamingSpecsViewModel> maybeStreamingSpecs =
            await mediator.Send(new GetChannelStreamingSpecs(request.ChannelNumber));
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
#EXT-X-VERSION:6
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=""subs"",NAME=""English"",DEFAULT=YES,AUTOSELECT=YES,FORCED=NO,LANGUAGE=""en"",URI=""{subtitlePlaylist}""
#EXT-X-STREAM-INF:BANDWIDTH={bitrate}{resolution}
{variantPlaylist}";
    }
}
