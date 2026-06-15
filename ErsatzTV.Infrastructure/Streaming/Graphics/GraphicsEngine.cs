using System.IO.Pipelines;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class GraphicsEngine(
    TemplateFunctions templateFunctions,
    GraphicsEngineFonts graphicsEngineFonts,
    ITempFilePool tempFilePool,
    IConfigElementRepository configElementRepository,
    ILocalStatisticsProvider localStatisticsProvider,
    ILogger<GraphicsEngine> logger)
    : IGraphicsEngine
{
    public async Task Run(GraphicsEngineContext context, PipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        graphicsEngineFonts.LoadFonts(FileSystemLayout.FontsCacheFolder);

        Option<string> ffprobePath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);

        Option<string> ffmpegPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);

        var elements = new List<IGraphicsElement>();
        foreach (GraphicsElementContext element in context.Elements)
        {
            switch (element)
            {
                case WatermarkElementContext watermarkElementContext:
                    var watermark = new WatermarkElement(watermarkElementContext.Options, logger);
                    if (watermark.IsValid)
                    {
                        elements.Add(watermark);
                    }

                    break;

                case ImageElementDataContext imageElementContext:
                    elements.Add(new ImageElement(imageElementContext.ImageElement, logger));
                    break;

                case TextElementDataContext textElementContext:
                    elements.Add(new TextElement(graphicsEngineFonts, textElementContext.TextElement, logger));
                    break;

                case MotionElementDataContext motionElementDataContext:
                    elements.Add(
                        new MotionElement(
                            motionElementDataContext.MotionElement,
                            ffprobePath,
                            ffmpegPath,
                            localStatisticsProvider,
                            logger));
                    break;

                case ScriptElementDataContext scriptElementDataContext:
                    elements.Add(new ScriptElement(scriptElementDataContext.ScriptElement, logger));
                    break;

                case SubtitleElementDataContext subtitleElementContext:
                {
                    var variables = context.TemplateVariables.ToDictionary();
                    foreach (KeyValuePair<string, string> variable in subtitleElementContext.Variables)
                    {
                        variables.Add(variable.Key, variable.Value);
                    }

                    var subtitleElement = new SubtitleElement(
                        templateFunctions,
                        tempFilePool,
                        subtitleElementContext.SubtitleElement,
                        ffmpegPath,
                        variables,
                        logger);

                    elements.Add(subtitleElement);
                    break;
                }
            }
        }

        // initialize all elements
        await Task.WhenAll(elements.Select(e => e.InitializeAsync(context, cancellationToken)));

        long frameCount = 0;
        var totalFrames = (long)(context.Duration.TotalSeconds * context.FrameRate.ParsedFrameRate);

        int width = context.FrameSize.Width;
        int height = context.FrameSize.Height;
        int frameBufferSize = width * height * 4; // BGRA = 4 bytes
        var skImageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        using var paint = new SKPaint();
        var preparedElementImages = new List<PreparedElementImage>(elements.Count);

        var prepareTasks = new List<Task<Option<PreparedElementImage>>>(elements.Count);

        foreach (IGraphicsElement element in elements.OrderBy(e => e.ZIndex))
        {
            logger.LogDebug(
                "Graphics element {Element} will draw with ZIndex {ZIndex}",
                element.DebugKey,
                element.ZIndex);
        }

        try
        {
            // `content_total_seconds` - the total number of seconds in the content
            TimeSpan contentTotalTime = context.Seek + context.ContentTotalDuration;

            while (!cancellationToken.IsCancellationRequested && frameCount < totalFrames)
            {
                // seconds since this specific stream started
                double streamTimeSeconds = frameCount / context.FrameRate.ParsedFrameRate;
                var streamTime = TimeSpan.FromSeconds(streamTimeSeconds);

                // `content_seconds` - the total number of seconds the frame is into the content
                TimeSpan contentTime = context.Seek + streamTime;

                // `time_of_day_seconds` - the total number of seconds the frame is since midnight
                DateTimeOffset frameTime = context.ContentStartTime + contentTime;

                // `channel_seconds` - the total number of seconds the frame is from when the channel started/activated
                TimeSpan channelTime = frameTime - context.ChannelStartTime;

                // prepare images outside mutate to allow async image generation
                prepareTasks.Clear();
                foreach (var element in elements)
                {
                    if (!element.IsFinished)
                    {
                        Task<Option<PreparedElementImage>> task = SafePrepareImage(
                            element,
                            frameTime.TimeOfDay,
                            contentTime,
                            contentTotalTime,
                            channelTime,
                            cancellationToken);

                        prepareTasks.Add(task);
                    }
                }

                Option<PreparedElementImage>[] results = await Task.WhenAll(prepareTasks);

                preparedElementImages.Clear();
                foreach (Option<PreparedElementImage> result in results)
                {
                    foreach (var preparedImage in result)
                    {
                        preparedElementImages.Add(preparedImage);
                    }
                }

                preparedElementImages.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));

                Memory<byte> memory = pipeWriter.GetMemory(frameBufferSize);

                unsafe
                {
                    using (System.Buffers.MemoryHandle handle = memory.Pin())
                    {
                        using (var surface = SKSurface.Create(skImageInfo, (IntPtr)handle.Pointer, width * 4))
                        {
                            if (surface == null)
                            {
                                logger.LogWarning("Failed to create SKSurface for frame");
                            }
                            else
                            {
                                var canvas = surface.Canvas;
                                canvas.Clear(SKColors.Transparent);

                                foreach (PreparedElementImage preparedImage in preparedElementImages)
                                {
                                    // Optimization: Skip BlendMode if opacity is full
                                    if (preparedImage.Opacity < 0.99f)
                                    {
                                        using var colorFilter = SKColorFilter.CreateBlendMode(
                                            SKColors.White.WithAlpha((byte)(preparedImage.Opacity * 255)),
                                            SKBlendMode.Modulate);
                                        paint.ColorFilter = colorFilter;
                                        canvas.DrawBitmap(preparedImage.Image, preparedImage.Point, paint);
                                    }
                                    else
                                    {
                                        paint.ColorFilter = null;
                                        canvas.DrawBitmap(preparedImage.Image, preparedImage.Point, paint);
                                    }

                                    if (preparedImage.Dispose)
                                    {
                                        preparedImage.Image.Dispose();
                                    }
                                }
                            }
                        }
                    }
                }

                pipeWriter.Advance(frameBufferSize);
                await pipeWriter.FlushAsync(cancellationToken);

                frameCount++;
            }
        }
        catch (Exception)
        {
            // do nothing; don't want to throw on a background task
        }
        finally
        {
            await pipeWriter.CompleteAsync();

            foreach (IDisposable element in elements.OfType<IDisposable>())
            {
                element.Dispose();
            }
        }
    }

    private async Task<Option<PreparedElementImage>> SafePrepareImage(
        IGraphicsElement element,
        TimeSpan frameTimeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken ct)
    {
        try
        {
            return await element.PrepareImage(
                frameTimeOfDay,
                contentTime,
                contentTotalTime,
                channelTime,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render element {Type}. Disabling.", element.GetType().Name);

            element.IsFinished = true;

            return Option<PreparedElementImage>.None;
        }
    }
}
