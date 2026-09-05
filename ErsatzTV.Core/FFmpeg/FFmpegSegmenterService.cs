using System.Collections.Concurrent;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.FFmpeg;

public class FFmpegSegmenterService(ILogger<FFmpegSegmenterService> logger) : IFFmpegSegmenterService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();
    private readonly ConcurrentDictionary<string, Session> _sessionWorkers = new();

    public event EventHandler OnWorkersChanged;

    public ICollection<IHlsSessionWorker> Workers => _sessionWorkers.Values.Select(session => session.Worker).ToList();

    public async Task<IDisposable> LockForStart(string channelNumber, CancellationToken cancellationToken)
    {
        SemaphoreSlim slim = _startLocks.GetOrAdd(channelNumber, _ => new SemaphoreSlim(1, 1));
        await slim.WaitAsync(cancellationToken);
        return new StartLockReleaser(slim);
    }

    public bool TryGetWorker(string channelNumber, out IHlsSessionWorker worker)
    {
        bool found = _sessionWorkers.TryGetValue(channelNumber, out Session session);
        worker = session?.Worker;
        return found;
    }

    public async Task<Either<BaseError, Unit>> WaitForReady(
        string channelNumber,
        IHlsSessionWorker worker,
        int initialSegmentCount,
        TimeSpan startDeadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessionWorkers.TryGetValue(channelNumber, out Session session) ||
            !ReferenceEquals(session.Worker, worker))
        {
            return new SessionEndedBeforeReady(channelNumber);
        }

        if (session.IsReady)
        {
            return Unit.Default;
        }

        Either<BaseError, Unit> result = await SessionStartWait.ForReady(
            channelNumber, worker, session.Ended.Task, initialSegmentCount, startDeadline, cancellationToken);
        if (result.IsRight)
        {
            session.IsReady = true;
        }

        return result;
    }

    public bool TryAddWorker(string channelNumber, IHlsSessionWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        bool result = _sessionWorkers.TryAdd(channelNumber, new Session(worker));
        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public void RemoveWorker(string channelNumber, IHlsSessionWorker worker)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out Session session) &&
            ReferenceEquals(session.Worker, worker) &&
            _sessionWorkers.TryRemove(new KeyValuePair<string, Session>(channelNumber, session)))
        {
            session.Ended.TrySetResult();
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsActive(string channelNumber) => _sessionWorkers.ContainsKey(channelNumber);

    public async Task<bool> StopChannel(string channelNumber, CancellationToken cancellationToken)
    {
        if (TryGetWorker(channelNumber, out IHlsSessionWorker worker))
        {
            await worker.Cancel(cancellationToken);
            return true;
        }

        return false;
    }

    public void TouchChannel(string channelNumber, string fileName)
    {
        if (TryGetWorker(channelNumber, out IHlsSessionWorker worker))
        {
            worker.Touch(fileName);
        }
    }

    public void PlayoutUpdated(string channelNumber)
    {
        if (TryGetWorker(channelNumber, out IHlsSessionWorker worker))
        {
            logger.LogInformation(
                "Playout has been updated for channel {ChannelNumber}, HLS segmenter will skip ahead to catch up",
                channelNumber);

            worker.PlayoutUpdated();
        }
    }

    private sealed class Session(IHlsSessionWorker worker)
    {
        public IHlsSessionWorker Worker { get; } = worker;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool IsReady;
    }
}
