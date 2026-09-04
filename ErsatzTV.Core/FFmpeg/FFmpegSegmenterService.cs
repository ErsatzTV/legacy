using System.Collections.Concurrent;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.FFmpeg;

public class FFmpegSegmenterService(ILogger<FFmpegSegmenterService> logger) : IFFmpegSegmenterService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();
    private readonly ConcurrentDictionary<string, IHlsSessionWorker> _sessionWorkers = new();

    public event EventHandler OnWorkersChanged;

    public ICollection<IHlsSessionWorker> Workers => _sessionWorkers.Values;

    public async Task<IDisposable> LockForStart(string channelNumber, CancellationToken cancellationToken)
    {
        SemaphoreSlim slim = _startLocks.GetOrAdd(channelNumber, _ => new SemaphoreSlim(1, 1));
        await slim.WaitAsync(cancellationToken);
        return new StartLockReleaser(slim);
    }

    public bool TryGetWorker(string channelNumber, out IHlsSessionWorker worker) =>
        _sessionWorkers.TryGetValue(channelNumber, out worker);

    public bool TryAddWorker(string channelNumber, IHlsSessionWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        bool result = _sessionWorkers.TryAdd(channelNumber, worker);
        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public void RemoveWorker(string channelNumber, IHlsSessionWorker worker)
    {
        if (_sessionWorkers.TryRemove(new KeyValuePair<string, IHlsSessionWorker>(channelNumber, worker)))
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsActive(string channelNumber) => _sessionWorkers.ContainsKey(channelNumber);

    public async Task<bool> StopChannel(string channelNumber, CancellationToken cancellationToken)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            await worker.Cancel(cancellationToken);
            return true;
        }

        return false;
    }

    public void TouchChannel(string channelNumber, string fileName)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            worker.Touch(fileName);
        }
    }

    public void PlayoutUpdated(string channelNumber)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            logger.LogInformation(
                "Playout has been updated for channel {ChannelNumber}, HLS segmenter will skip ahead to catch up",
                channelNumber);

            worker.PlayoutUpdated();
        }
    }
}
