using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Notifications;
using ErsatzTV.FFmpeg.Runtime;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Events;

namespace ErsatzTV.Application.Troubleshooting;

public class StartTroubleshootingPlaybackHandler(
    ITroubleshootingNotifier notifier,
    IMediator mediator,
    IEntityLocker entityLocker,
    IRuntimeInfo runtimeInfo,
    IGraphicsEngine graphicsEngine,
    InMemoryLogService logService,
    LoggingLevelSwitches loggingLevelSwitches,
    ILocalFileSystem localFileSystem,
    IFileSystem fileSystem,
    ILogger<StartTroubleshootingPlaybackHandler> logger)
    : IRequestHandler<StartTroubleshootingPlayback>
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public async Task Handle(StartTroubleshootingPlayback request, CancellationToken cancellationToken)
    {
        var currentStreamingLevel = loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel;
        loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel = LogEventLevel.Debug;

        try
        {
            using var logContext = LogContext.PushProperty(InMemoryLogService.CorrelationIdKey, request.SessionId);

            // write media info without title
            foreach (var mediaInfo in request.MediaItemInfo)
            {
                string infoJson = JsonSerializer.Serialize(mediaInfo with { Title = null }, Options);
                await File.WriteAllTextAsync(
                    Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "media_info.json"),
                    infoJson,
                    cancellationToken);
            }

            // write troubleshooting info
            string troubleshootingInfoJson = JsonSerializer.Serialize(
                new
                {
                    request.TroubleshootingInfo.Version,
                    request.TroubleshootingInfo.NextVersion,
                    request.TroubleshootingInfo.FFmpegVersion,
                    Environment = request.TroubleshootingInfo.Environment.OrderBy(x => x.Key)
                        .ToDictionary(x => x.Key, x => x.Value),
                    request.TroubleshootingInfo.Health,
                    request.TroubleshootingInfo.Cpus,
                    request.TroubleshootingInfo.VideoControllers,
                    request.TroubleshootingInfo.FFmpegSettings,
                    request.TroubleshootingInfo.FFmpegProfiles,
                    request.TroubleshootingInfo.Watermarks
                },
                Options);
            await File.WriteAllTextAsync(
                Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "troubleshooting_info.json"),
                troubleshootingInfoJson,
                cancellationToken);

            // write stream selector
            if (!string.IsNullOrWhiteSpace(request.StreamSelector))
            {
                string fullPath = Path.Combine(FileSystemLayout.ChannelStreamSelectorsFolder, request.StreamSelector);
                if (File.Exists(fullPath))
                {
                    File.Copy(
                        fullPath,
                        Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "stream-selector.yml"));
                }
            }

            HardwareAccelerationKind hwAccel = request.TroubleshootingInfo.FFmpegProfiles.Head().HardwareAcceleration;
            if (hwAccel is HardwareAccelerationKind.Qsv)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "capabilities_qsv.txt"),
                    request.TroubleshootingInfo.QsvCapabilities,
                    cancellationToken);
            }

            if (hwAccel is HardwareAccelerationKind.Vaapi || hwAccel is HardwareAccelerationKind.Qsv &&
                runtimeInfo.IsOSPlatform(OSPlatform.Linux))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "capabilities_vaapi.txt"),
                    request.TroubleshootingInfo.VaapiCapabilities,
                    cancellationToken);
            }

            if (hwAccel is HardwareAccelerationKind.Nvenc)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "capabilities_nvidia.txt"),
                    request.TroubleshootingInfo.NvidiaCapabilities,
                    cancellationToken);
            }

            if (request.StreamingEngine is StreamingEngine.Legacy)
            {
                logger.LogDebug(
                    "ffmpeg troubleshooting arguments {FFmpegArguments}",
                    request.PlayoutItemResult.Process.Arguments);
            }

            Option<Pipe> maybePipe = Option<Pipe>.None;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                Command processWithPipe = request.PlayoutItemResult.Process;
                foreach (GraphicsEngineContext graphicsEngineContext in request.PlayoutItemResult.GraphicsEngineContext)
                {
                    var pipe = new Pipe();
                    maybePipe = pipe;
                    processWithPipe =
                        processWithPipe.WithStandardInputPipe(PipeSource.FromStream(pipe.Reader.AsStream()));

                    // fire and forget graphics engine task
                    _ = graphicsEngine.Run(
                        graphicsEngineContext,
                        pipe.Writer,
                        linkedCts.Token);
                }

                var progressParser = new FFmpegProgress();

                var outputPipe = request.StreamingEngine is StreamingEngine.Legacy
                    ? PipeTarget.ToDelegate(progressParser.ParseLine)
                    : PipeTarget.ToDelegate(l => NextLogger.LogNextLine(l, logger));

                var errorPipe = request.StreamingEngine is StreamingEngine.Legacy
                    ? PipeTarget.Null
                    : PipeTarget.ToDelegate(l => NextLogger.LogNextLine(l, logger));

                CommandResult commandResult = await processWithPipe
                    .WithWorkingDirectory(FileSystemLayout.TranscodeTroubleshootingFolder)
                    .WithStandardErrorPipe(errorPipe)
                    .WithStandardOutputPipe(outputPipe)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(linkedCts.Token);

                string processName = request.StreamingEngine is StreamingEngine.Legacy
                    ? "ffmpeg"
                    : "ersatztv-channel";

                logger.LogDebug(
                    "Troubleshooting playback ({ProcessName}) completed with exit code {ExitCode}",
                    processName,
                    commandResult.ExitCode);

                if (request.StreamingEngine is StreamingEngine.Next)
                {
                    foreach (string dir in localFileSystem.ListSubdirectories(
                                 FileSystemLayout.TranscodeTroubleshootingFolder))
                    {
                        foreach (string file in localFileSystem.ListFiles(dir, "ffreport.log"))
                        {
                            foreach (string line in await fileSystem.File.ReadAllLinesAsync(file, cancellationToken))
                            {
                                progressParser.ParseLine(line);
                            }

                            break;
                        }
                    }
                }

                progressParser.LogSpeed(
                    request.MediaItemInfo.Map(i => i.Id),
                    true,
                    FileSystemLayout.TranscodeTroubleshootingChannel,
                    logger);

                try
                {
                    IEnumerable<string> logs = logService.Sink.GetLogs(request.SessionId);
                    await File.WriteAllLinesAsync(
                        Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "logs.txt"),
                        logs,
                        linkedCts.Token);
                    logService.Sink.ClearLogs(request.SessionId);
                }
                catch (Exception)
                {
                    // do nothing
                }

                await mediator.Publish(
                    new PlaybackTroubleshootingCompletedNotification(
                        commandResult.ExitCode,
                        Option<Exception>.None,
                        progressParser.Speed),
                    linkedCts.Token);

                if (commandResult.ExitCode != 0)
                {
                    await linkedCts.CancelAsync();
                    notifier.NotifyFailed(request.SessionId);
                }
            }
            catch (TaskCanceledException)
            {
                // do nothing
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                foreach (Pipe pipe in maybePipe)
                {
                    await pipe.Writer.CompleteAsync();
                }
            }
        }
        finally
        {
            entityLocker.UnlockTroubleshootingPlayback();
            loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel = currentStreamingLevel;
        }
    }
}
