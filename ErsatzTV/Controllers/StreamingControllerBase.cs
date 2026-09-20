using System.IO.Pipelines;
using System.Text;
using CliWrap;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

public abstract class StreamingControllerBase(IGraphicsEngine graphicsEngine, ILogger logger)
    : ControllerBase
{
    protected IActionResult GetProcessResponse(
        Either<BaseError, PlayoutItemProcessModel> result,
        string channelNumber,
        StreamingMode mode)
    {
        foreach (BaseError error in result.LeftToSeq())
        {
            logger.LogError(
                "Failed to create stream for channel {ChannelNumber}: {Error}",
                channelNumber,
                error.Value);

            return BadRequest(error.Value);
        }

        foreach (PlayoutItemProcessModel processModel in result.RightToSeq())
        {
            return StartPlayout(processModel);
        }

        // this will never happen
        return new NotFoundResult();
    }

    private FileStreamResult StartPlayout(PlayoutItemProcessModel processModel)
    {
        // for process counter
        var ffmpegProcess = new FFmpegProcess();
        Command process = processModel.Process;

        logger.LogDebug("ffmpeg arguments {FFmpegArguments}", process.Arguments);

        var cts = new CancellationTokenSource();

        // do not use 'using' here; the token needs to live longer than this method scope
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token,
            HttpContext.RequestAborted);

        var pipe = new Pipe();
        var stdErrBuffer = new StringBuilder();

        Option<Pipe> maybeGePipe = Option<Pipe>.None;

        Command processWithPipe = process;
        foreach (GraphicsEngineContext graphicsEngineContext in processModel.GraphicsEngineContext)
        {
            var gePipe = new Pipe();
            maybeGePipe = gePipe;
            processWithPipe = process.WithStandardInputPipe(PipeSource.FromStream(gePipe.Reader.AsStream()));

            // fire and forget graphics engine task
            _ = graphicsEngine.Run(
                graphicsEngineContext,
                gePipe.Writer,
                linkedCts.Token);
        }

        CommandTask<CommandResult> task = processWithPipe
            .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(linkedCts.Token);

        // ensure cleanup happens when ffmpeg exits (either naturally or via cancellation)
        _ = task.Task.ContinueWith(
            (t, _) =>
            {
                foreach (Pipe gePipe in maybeGePipe)
                {
                    gePipe.Writer.Complete(t.Exception);
                }

                pipe.Writer.Complete(t.Exception);
                ffmpegProcess.Dispose();

                linkedCts.Cancel();
                linkedCts.Dispose();

                cts.Cancel();
                cts.Dispose();
            },
            null,
            TaskScheduler.Default);

        return new FileStreamResult(pipe.Reader.AsStream(), "video/mp2t");
    }
}
