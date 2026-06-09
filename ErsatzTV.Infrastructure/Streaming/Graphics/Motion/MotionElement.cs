using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class MotionElement(
    MotionGraphicsElement motionElement,
    Option<string> ffprobePath,
    Option<string> ffmpegPath,
    ILocalStatisticsProvider localStatisticsProvider,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private CancellationTokenSource _cancellationTokenSource;
    private CommandTask<CommandResult> _commandTask;
    private int _frameSize;
    private PipeReader _pipeReader;
    private SKPointI _point;
    private SKBitmap _motionFrameBitmap;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private MotionElementState _state;

    public override int ZIndex { get; } = motionElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Motion {motionElement.DebugName()}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _pipeReader?.Complete();

        _cancellationTokenSource?.Cancel();
        try
        {
#pragma warning disable VSTHRD002
            _commandTask?.Task.Wait();
#pragma warning restore VSTHRD002
        }
        catch (Exception)
        {
            // do nothing
        }

        _cancellationTokenSource?.Dispose();

        _motionFrameBitmap?.Dispose();
    }

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        try
        {
            _startTime = TimeSpan.FromSeconds(motionElement.StartSeconds ?? 0);
            var holdDuration = TimeSpan.FromSeconds(motionElement.HoldSeconds ?? 0);
            ProbeResult probeResult = await ProbeMotionElement(context.FrameSize);
            var overlayDuration = motionElement.EndBehavior switch
            {
                MotionEndBehavior.Loop => context.Seek + context.Duration,
                MotionEndBehavior.Hold => probeResult.Duration + holdDuration,
                _ => probeResult.Duration
            };

            _endTime = _startTime + overlayDuration;

            // already past the time when this is supposed to play; don't do any more work
            if (_startTime + overlayDuration < context.Seek)
            {
                IsFinished = true;
                return;
            }

            var pipe = new Pipe();
            _pipeReader = pipe.Reader;

            var overlaySeekTime = TimeSpan.Zero;
            if (_startTime < context.Seek)
            {
                overlaySeekTime = context.Seek - _startTime;
            }

            Resolution sourceSize = probeResult.Size;

            int scaledWidth = sourceSize.Width;
            int scaledHeight = sourceSize.Height;

            if (motionElement.Scale)
            {
                scaledWidth = (int)Math.Round(
                    (motionElement.ScaleWidthPercent ?? 100) / 100.0 * context.FrameSize.Width);
                double aspectRatio = (double)sourceSize.Height / sourceSize.Width;
                scaledHeight = (int)Math.Round(scaledWidth * aspectRatio);
            }

            // ensure even dimensions
            if (scaledWidth % 2 != 0)
            {
                scaledWidth++;
            }

            if (scaledHeight % 2 != 0)
            {
                scaledHeight++;
            }

            var targetSize = new Resolution { Width = scaledWidth, Height = scaledHeight };

            _frameSize = targetSize.Width * targetSize.Height * 4;

            _motionFrameBitmap = new SKBitmap(
                targetSize.Width,
                targetSize.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul);

            _point = SKPointI.Empty;

            (int horizontalMargin, int verticalMargin) = NormalMargins(
                context.FrameSize,
                motionElement.HorizontalMarginPercent ?? 0,
                motionElement.VerticalMarginPercent ?? 0);

            _point = CalculatePosition(
                motionElement.Location,
                context.FrameSize.Width,
                context.FrameSize.Height,
                targetSize.Width,
                targetSize.Height,
                horizontalMargin,
                verticalMargin);

            List<string> arguments = ["-nostdin", "-hide_banner", "-nostats", "-loglevel", "error"];

            if (motionElement.EndBehavior is MotionEndBehavior.Loop)
            {
                arguments.AddRange(["-stream_loop", "-1"]);
            }

            foreach (string decoder in probeResult.Decoder)
            {
                arguments.AddRange(["-c:v", decoder]);
            }

            if (overlaySeekTime > TimeSpan.Zero)
            {
                arguments.AddRange(["-ss", overlaySeekTime.TotalSeconds.ToString(CultureInfo.InvariantCulture)]);
            }

            arguments.AddRange(
            [
                "-i", motionElement.VideoPath,
            ]);

            var videoFilter = $"fps={context.FrameRate.RFrameRate}";
            if (motionElement.Scale)
            {
                videoFilter += $",scale={targetSize.Width}:{targetSize.Height}";
            }

            arguments.AddRange(["-vf", videoFilter]);

            if (motionElement.EndBehavior is MotionEndBehavior.Loop)
            {
                arguments.AddRange(
                    "-t",
                    $"{(int)context.Duration.TotalHours:00}:{context.Duration:mm}:{context.Duration:ss\\.fffffff}");
            }

            arguments.AddRange(
            [
                "-f", "image2pipe",
                "-pix_fmt", "bgra",
                "-vcodec", "rawvideo",
                "-"
            ]);

            _state = MotionElementState.PlayingIn;

            Command command = Cli.Wrap(await ffmpegPath.IfNoneAsync("ffmpeg"))
                .WithArguments(arguments)
                .WithWorkingDirectory(FileSystemLayout.TempFilePoolFolder)
                .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()));

            //logger.LogDebug("ffmpeg motion element arguments {FFmpegArguments}", command.Arguments);

            _cancellationTokenSource = new CancellationTokenSource();
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellationTokenSource.Token);

            _commandTask = command.ExecuteAsync(linkedToken.Token);

            _ = _commandTask.Task.ContinueWith(_ => pipe.Writer.Complete(), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            IsFinished = true;
            logger.LogWarning(ex, "Failed to initialize motion element; will disable for this content");
        }
    }

    public override async ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_state is MotionElementState.Finished || contentTime < _startTime)
            {
                return Option<PreparedElementImage>.None;
            }

            if (_state is MotionElementState.Holding)
            {
                if (contentTime <= _endTime)
                {
                    return new PreparedElementImage(_motionFrameBitmap, _point, 1.0f, ZIndex, false);
                }

                _state = MotionElementState.Finished;
                return Option<PreparedElementImage>.None;
            }

            while (true)
            {
                ReadResult readResult = await _pipeReader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = readResult.Buffer;
                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                try
                {
                    if (buffer.Length >= _frameSize)
                    {
                        ReadOnlySequence<byte> sequence = buffer.Slice(0, _frameSize);

                        using (SKPixmap pixmap = _motionFrameBitmap.PeekPixels())
                        {
                            sequence.CopyTo(pixmap.GetPixelSpan());
                        }

                        // mark this frame as consumed
                        consumed = sequence.End;

                        // we are done, return the frame
                        return new PreparedElementImage(_motionFrameBitmap, _point, 1.0f, ZIndex, false);
                    }

                    if (readResult.IsCompleted)
                    {
                        await _pipeReader.CompleteAsync();

                        if (motionElement.EndBehavior is MotionEndBehavior.Hold)
                        {
                            _state = MotionElementState.Holding;
                            return new PreparedElementImage(_motionFrameBitmap, _point, 1.0f, ZIndex, false);
                        }
                        else
                        {
                            _state = MotionElementState.Finished;
                        }

                        return Option<PreparedElementImage>.None;
                    }
                }
                finally
                {
                    if (_state is not (MotionElementState.Finished or MotionElementState.Holding))
                    {
                        // advance the reader, consuming the processed frame and examining the entire buffer
                        _pipeReader.AdvanceTo(consumed, examined);
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            return Option<PreparedElementImage>.None;
        }
    }

    private async Task<ProbeResult> ProbeMotionElement(Resolution frameSize)
    {
        try
        {
            foreach (string ffprobe in ffprobePath)
            {
                Either<BaseError, MediaVersion> maybeMediaVersion =
                    await localStatisticsProvider.GetStatistics(ffprobe, motionElement.VideoPath);

                foreach (var mediaVersion in maybeMediaVersion.RightToSeq())
                {
                    Option<string> decoder = Option<string>.None;

                    foreach (var videoStream in mediaVersion.Streams.Where(s =>
                                 s.MediaStreamKind is MediaStreamKind.Video))
                    {
                        decoder = videoStream.Codec switch
                        {
                            "vp8" => "libvpx",
                            "vp9" => "libvpx-vp9",
                            _ => Option<string>.None
                        };
                    }

                    return new ProbeResult(
                        new Resolution { Width = mediaVersion.Width, Height = mediaVersion.Height },
                        decoder,
                        mediaVersion.Duration);
                }
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return new ProbeResult(frameSize, Option<string>.None, TimeSpan.Zero);
    }

    private record ProbeResult(Resolution Size, Option<string> Decoder, TimeSpan Duration);
}
