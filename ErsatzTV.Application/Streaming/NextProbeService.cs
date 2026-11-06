using System.IO.Abstractions;
using System.Text;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next.Description;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class NextProbeService(IFileSystem fileSystem, ILogger<NextProbeService> logger)
    : NextChannelHandlerBase(fileSystem), INextProbeService
{
    // next limits each probe itself; this only catches a hung binary
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly Lock _sync = new();
    private Option<NextProbeResult> _latest;
    private long _latestGeneration;
    private long _generation;
    private string _inFlightPath;
    private Task<Option<NextProbeResult>> _inFlight;

    public Option<NextProbeResult> Latest
    {
        get
        {
            lock (_sync)
            {
                return _latest;
            }
        }
    }

    public Task<Option<NextProbeResult>> Refresh(string ffmpegPath, CancellationToken cancellationToken)
    {
        Task<Option<NextProbeResult>> task;
        lock (_sync)
        {
            if (_inFlight is not null && _inFlightPath == ffmpegPath)
            {
                task = _inFlight;
            }
            else
            {
                long generation = ++_generation;
                task = Task.Run(() => ProbeAndStore(ffmpegPath, generation), CancellationToken.None);
                _inFlight = task;
                _inFlightPath = ffmpegPath;
            }
        }

        // shared probe: one caller's cancel must not stop it for the others
        return task.WaitAsync(cancellationToken);
    }

    private async Task<Option<NextProbeResult>> ProbeAndStore(string ffmpegPath, long generation)
    {
        Option<(RuntimeCapability, string)> maybeProbe = Option<(RuntimeCapability, string)>.None;
        try
        {
            maybeProbe = await Probe(ffmpegPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to probe hardware with ersatztv-channel");
        }

        lock (_sync)
        {
            foreach ((RuntimeCapability capability, string json) in maybeProbe)
            {
                // an older path's probe can finish after a newer one
                if (generation > _latestGeneration)
                {
                    _latest = new NextProbeResult(capability, json, ffmpegPath, DateTimeOffset.Now);
                    _latestGeneration = generation;
                }
            }

            if (_generation == generation)
            {
                _inFlight = null;
                _inFlightPath = null;
            }

            return _latest;
        }
    }

    protected virtual async Task<Option<(RuntimeCapability Capability, string Json)>> Probe(string ffmpegPath)
    {
        Validation<BaseError, string> validation = await ChannelBinaryMustExist();
        foreach (string channelBinary in validation.SuccessToSeq())
        {
            using var cts = new CancellationTokenSource(Timeout);

            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();
            CommandResult command = await Cli.Wrap(channelBinary)
                .WithArguments(["describe", "--probe", "--ffmpeg", ffmpegPath])
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cts.Token);

            if (!command.IsSuccess)
            {
                logger.LogWarning(
                    "ersatztv-channel describe --probe exited with code {ExitCode}: {Error}",
                    command.ExitCode,
                    stdErrBuffer.ToString().Trim());
                return Option<(RuntimeCapability, string)>.None;
            }

            string json = stdOutBuffer.ToString();
            Describe describe = Describe.FromJson(json);
            if (describe?.DescribeVersion != NextContract.DescribeVersion)
            {
                logger.LogWarning(
                    "ersatztv-channel describe version {Version} is not supported",
                    describe?.DescribeVersion);
                return Option<(RuntimeCapability, string)>.None;
            }

            foreach (RuntimeCapability capability in Optional(describe.RuntimeCapability))
            {
                var available = capability.Accels
                    .Filter(a => a.Status == AccelStatus.Available)
                    .Map(a => string.Join(' ', new[] { a.Accel, a.Device, a.Driver }.Filter(s => s is not null)))
                    .ToList();

                logger.LogInformation(
                    "ErsatzTV Next hardware probe found {Available}",
                    available.Count > 0 ? string.Join(", ", available) : "no available hardware acceleration");
            }

            return Optional(describe.RuntimeCapability).Map(c => (c, json));
        }

        return Option<(RuntimeCapability, string)>.None;
    }
}
