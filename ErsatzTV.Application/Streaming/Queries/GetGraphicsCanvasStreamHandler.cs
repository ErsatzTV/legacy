using System.IO.Pipelines;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetGraphicsCanvasStreamHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IWatermarkSelector watermarkSelector,
    IGraphicsElementSelector graphicsElementSelector,
    IGraphicsEngineContextFactory graphicsEngineContextFactory,
    IGraphicsEngine graphicsEngine,
    IFFmpegSegmenterService ffmpegSegmenterService,
    ITroubleshootingPlayoutItemStore troubleshootingPlayoutItemStore,
    ILogger<GetGraphicsCanvasStreamHandler> logger)
    : IRequestHandler<GetGraphicsCanvasStream, Option<Stream>>
{
    public async Task<Option<Stream>> Handle(GetGraphicsCanvasStream request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<string> maybeFFmpegPath = await dbContext.ConfigElements
            .GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken);
        if (maybeFFmpegPath.IsNone)
        {
            logger.LogWarning("Unable to stream graphics canvas; ffmpeg path is not configured");
            return None;
        }

        Option<CanvasItem> maybeCanvasItem = request.ChannelNumber == FileSystemLayout.TranscodeTroubleshootingChannel
            ? troubleshootingPlayoutItemStore.Current().Map(t => new CanvasItem(t.Channel, t.PlayoutItem, None))
            : await LoadCanvasItem(dbContext, request, cancellationToken);

        foreach ((Channel channel, PlayoutItem playoutItem, Option<ChannelWatermark> maybeGlobalWatermark) in maybeCanvasItem)
        {
            DateTimeOffset contentStart = playoutItem.StartOffset;
            DateTimeOffset now = contentStart + request.Offset;
            DateTimeOffset finish = playoutItem.FinishOffset < now + request.Duration
                ? playoutItem.FinishOffset
                : now + request.Duration;

            // a stale offset past the item's finish still gets the requested duration so
            // next's ffmpeg never sees an empty stream
            if (finish <= now)
            {
                logger.LogWarning(
                    "Graphics canvas request for playout item {PlayoutItemId} at offset {Offset} is past item finish {Finish}",
                    playoutItem.Id,
                    request.Offset,
                    playoutItem.FinishOffset);

                finish = now + request.Duration;
            }

            List<WatermarkOptions> watermarks = watermarkSelector.SelectWatermarks(
                maybeGlobalWatermark,
                channel,
                playoutItem,
                now,
                shouldLogMessages: false);

            List<PlayoutItemGraphicsElement> graphicsElements = graphicsElementSelector.SelectGraphicsElements(
                channel,
                playoutItem,
                now,
                shouldLogMessages: false);

            DateTimeOffset channelStartTime = contentStart;
            if (ffmpegSegmenterService.TryGetWorker(request.ChannelNumber, out IHlsSessionWorker worker))
            {
                channelStartTime = worker.GetModel().StartedAt;
            }

            MediaVersion headVersion = playoutItem.MediaItem.GetHeadVersion();
            // the engine derives content_total_seconds as Seek + ContentTotalDuration, so this is
            // the time remaining in the item (legacy passes finish - now), not the media duration
            TimeSpan contentTotalDuration = playoutItem.FinishOffset > now
                ? playoutItem.FinishOffset - now
                : finish - now;

            Option<GraphicsEngineContext> maybeContext = await graphicsEngineContextFactory.Create(
                channel,
                playoutItem.MediaItem,
                headVersion,
                watermarks,
                graphicsElements,
                request.FrameRate,
                channelStartTime,
                contentStart,
                playoutItem.FinishOffset,
                request.Offset,
                finish - now,
                contentTotalDuration,
                cancellationToken);

            // nothing selected (channel edited since sync): stream transparent frames rather
            // than an error, which would kill next's ffmpeg for the whole chunk
            GraphicsEngineContext context = maybeContext.IfNone(
                () => new GraphicsEngineContext(
                    channel.Number,
                    playoutItem.MediaItem,
                    Elements: [],
                    TemplateVariables: [],
                    channel.FFmpegProfile.Resolution,
                    channel.FFmpegProfile.Resolution,
                    request.FrameRate,
                    channelStartTime,
                    contentStart,
                    playoutItem.FinishOffset,
                    request.Offset,
                    finish - now,
                    contentTotalDuration));

            logger.LogDebug(
                "Streaming graphics canvas for channel {ChannelNumber} item {PlayoutItemId}: offset {Offset}, duration {Duration}, rate {FrameRate}, elements {ElementCount}",
                channel.Number,
                playoutItem.Id,
                request.Offset,
                finish - now,
                request.FrameRate.RFrameRate,
                context.Elements.Count);

            return StartCanvas(
                await maybeFFmpegPath.IfNoneAsync(string.Empty),
                channel.FFmpegProfile.Resolution,
                request.FrameRate,
                context,
                cancellationToken);
        }

        logger.LogWarning(
            "Unable to locate playout item {PlayoutItemId} for graphics canvas on channel {ChannelNumber}",
            request.PlayoutItemId,
            request.ChannelNumber);

        return None;
    }

    private sealed record CanvasItem(Channel Channel, PlayoutItem PlayoutItem, Option<ChannelWatermark> GlobalWatermark);

    private static async Task<Option<CanvasItem>> LoadCanvasItem(
        TvContext dbContext,
        GetGraphicsCanvasStream request,
        CancellationToken cancellationToken)
    {
        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.FFmpegProfile)
            .ThenInclude(p => p.Resolution)
            .Include(c => c.Watermark)
            .Include(c => c.Artwork)
            .Include(c => c.MirrorSourceChannel)
            .SelectOneAsync(c => c.Number, c => c.Number == request.ChannelNumber, cancellationToken);

        foreach (Channel channel in maybeChannel)
        {
            // mirror channels play the source channel's items shifted by the playout offset,
            // matching what SyncNextPlayoutHandler wrote for next
            TimeSpan playoutOffset = TimeSpan.Zero;
            string sourceChannelNumber = channel.Number;
            if (channel.PlayoutSource is ChannelPlayoutSource.Mirror && channel.MirrorSourceChannel is not null)
            {
                sourceChannelNumber = channel.MirrorSourceChannel.Number;
                playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;
            }

            Option<PlayoutItem> maybePlayoutItem = await dbContext.PlayoutItems
                .AsNoTracking()
                .Where(i => i.Id == request.PlayoutItemId && i.Playout.Channel.Number == sourceChannelNumber)
                .IncludeForNextPlayout()
                .AsSplitQuery()
                .SingleOrDefaultAsync(cancellationToken)
                .Map(Optional);

            foreach (PlayoutItem playoutItem in maybePlayoutItem)
            {
                playoutItem.Start += playoutOffset;
                playoutItem.Finish += playoutOffset;

                Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
                    .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
                    .BindT(watermarkId => dbContext.ChannelWatermarks
                        .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

                return new CanvasItem(channel, playoutItem, maybeGlobalWatermark);
            }
        }

        return None;
    }

    private Stream StartCanvas(
        string ffmpegPath,
        Resolution resolution,
        FrameRate frameRate,
        GraphicsEngineContext context,
        CancellationToken cancellationToken)
    {
        // for process counter
        var ffmpegProcess = new FFmpegProcess();

        var cts = new CancellationTokenSource();

        // do not use 'using' here; the token needs to live longer than this method scope
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

        var enginePipe = new Pipe();
        var outputPipe = new Pipe();

        // fire and forget graphics engine task
        _ = graphicsEngine.Run(context, enginePipe.Writer, linkedCts.Token);

        string[] arguments =
        [
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{resolution.Width}x{resolution.Height}",
            "-framerate", frameRate.RFrameRate,
            "-i", "pipe:0",
            "-c:v", "ffv1", "-level", "3", "-slices", "16", "-slicecrc", "0",
            "-pix_fmt", "bgra",
            "-f", "nut", "pipe:1"
        ];

        CommandTask<CommandResult> task = Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithStandardInputPipe(PipeSource.FromStream(enginePipe.Reader.AsStream()))
            .WithStandardOutputPipe(PipeTarget.ToStream(outputPipe.Writer.AsStream()))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => logger.LogWarning("Graphics canvas ffmpeg: {Line}", line)))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(linkedCts.Token);

        // ensure cleanup happens when ffmpeg exits (either naturally or via cancellation);
        // cancelling stops the engine if ffmpeg exited first
        _ = task.Task.ContinueWith(
            (t, _) =>
            {
                outputPipe.Writer.Complete(t.Exception);
                cts.Cancel();
                ffmpegProcess.Dispose();
                linkedCts.Dispose();
                cts.Dispose();
            },
            null,
            TaskScheduler.Default);

        return outputPipe.Reader.AsStream();
    }
}
