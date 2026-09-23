using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class SubtitleElement(
    TemplateFunctions templateFunctions,
    ITempFilePool tempFilePool,
    SubtitleGraphicsElement subtitleElement,
    Option<string> ffmpegPath,
    Dictionary<string, object> variables,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private CancellationTokenSource _cancellationTokenSource;
    private CommandTask<CommandResult> _commandTask;
    private int _frameSize;
    private PipeReader _pipeReader;
    private SKBitmap _videoFrame;
    private SKBitmap _visibleFrame;
    private bool _isFinished;

    public override int ZIndex { get; } = subtitleElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Subtitle {subtitleElement.DebugName()}";

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

        _visibleFrame?.Dispose();
        _videoFrame?.Dispose();
    }

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        try
        {
            // video size is the same as the main frame size
            _frameSize = context.FrameSize.Width * context.FrameSize.Height * 4;

            // buffer a few frames so ffmpeg can render ahead of the engine
            var pipe = new Pipe(
                new PipeOptions(
                    minimumSegmentSize: 1024 * 1024,
                    pauseWriterThreshold: _frameSize * 4L,
                    resumeWriterThreshold: _frameSize * 2L));
            _pipeReader = pipe.Reader;

            // blending onto a transparent black canvas makes ffmpeg's output premultiplied
            _videoFrame = new SKBitmap(
                context.FrameSize.Width,
                context.FrameSize.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            _visibleFrame = new SKBitmap();

            string subtitleTemplateFile = tempFilePool.GetNextTempFile(TempFileCategory.Subtitle);

            var scriptObject = new ScriptObject();
            scriptObject.Import(variables, renamer: member => member.Name);
            scriptObject.Import("convert_timezone", templateFunctions.ConvertTimeZone);
            scriptObject.Import("format_datetime", templateFunctions.FormatDateTime);

            var templateContext = new TemplateContext { MemberRenamer = member => member.Name };
            templateContext.PushGlobal(scriptObject);
            string inputText = await File.ReadAllTextAsync(subtitleElement.Template, cancellationToken);
            string textToRender = await Template.Parse(inputText).RenderAsync(templateContext);
            await File.WriteAllTextAsync(subtitleTemplateFile, textToRender, cancellationToken);

            string subtitleFile = Path.GetFileName(subtitleTemplateFile);
            string fontsDir = FileSystemLayout.FontsCacheFolder;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                fontsDir = fontsDir
                    .Replace(@"\", @"/\")
                    .Replace(@":/", @"\\:/");

                subtitleFile = subtitleFile
                    .Replace(@"\", @"/\")
                    .Replace(@":/", @"\\:/");
            }

            List<string> arguments =
            [
                "-nostdin", "-hide_banner", "-nostats", "-loglevel", "error",
                "-f", "lavfi",
                "-i",
                $"color=c=black@0.0:s={context.FrameSize.Width}x{context.FrameSize.Height}:r={context.FrameRate.RFrameRate},format=bgra,subtitles={subtitleFile}:fontsdir={fontsDir}:alpha=1",
                "-f", "image2pipe",
                "-pix_fmt", "bgra",
                "-vcodec", "rawvideo",
                "-"
            ];

            Command command = Cli.Wrap(await ffmpegPath.IfNoneAsync("ffmpeg"))
                .WithArguments(arguments)
                .WithWorkingDirectory(FileSystemLayout.TempFilePoolFolder)
                .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()));

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
            logger.LogWarning(ex, "Failed to initialize subtitle element; will disable for this content");
        }
    }

    public override async ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        if (_isFinished)
        {
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

                    SKRectI bounds;
                    using (SKPixmap pixmap = _videoFrame.PeekPixels())
                    {
                        Span<byte> pixels = pixmap.GetPixelSpan();
                        sequence.CopyTo(pixels);
                        bounds = GetVisibleBounds(pixels, pixmap.Width);
                    }

                    // mark this frame as consumed
                    consumed = sequence.End;
                    examined = consumed;

                    // compositing a full frame is expensive, so only hand over the visible region
                    if (bounds.IsEmpty)
                    {
                        return Option<PreparedElementImage>.None;
                    }

                    if (!_videoFrame.ExtractSubset(_visibleFrame, bounds))
                    {
                        return new PreparedElementImage(_videoFrame, SKPointI.Empty, 1.0f, ZIndex, false);
                    }

                    return new PreparedElementImage(
                        _visibleFrame,
                        new SKPointI(bounds.Left, bounds.Top),
                        1.0f,
                        ZIndex,
                        false);
                }

                if (readResult.IsCompleted)
                {
                    _isFinished = true;

                    await _pipeReader.CompleteAsync();
                    return Option<PreparedElementImage>.None;
                }
            }
            finally
            {
                if (!_isFinished)
                {
                    // leave unread frames available without waiting for more data
                    _pipeReader.AdvanceTo(consumed, examined);
                }
            }
        }
    }

    private static SKRectI GetVisibleBounds(ReadOnlySpan<byte> pixels, int width)
    {
        int stride = width * 4;

        int first = pixels.IndexOfAnyExcept((byte)0);
        if (first < 0)
        {
            return SKRectI.Empty;
        }

        int top = first / stride;
        int bottom = pixels.LastIndexOfAnyExcept((byte)0) / stride;

        int left = width;
        var right = 0;
        for (int y = top; y <= bottom; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, stride);
            int rowFirst = row.IndexOfAnyExcept((byte)0);
            if (rowFirst >= 0)
            {
                left = Math.Min(left, rowFirst / 4);
                right = Math.Max(right, row.LastIndexOfAnyExcept((byte)0) / 4);
            }
        }

        return new SKRectI(left, top, right + 1, bottom + 1);
    }
}
