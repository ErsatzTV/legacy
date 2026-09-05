namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface IFFmpegSegmenterService
{
    ICollection<IHlsSessionWorker> Workers { get; }
    event EventHandler OnWorkersChanged;
    Task<IDisposable> LockForStart(string channelNumber, CancellationToken cancellationToken);
    bool TryGetWorker(string channelNumber, out IHlsSessionWorker worker);
    Task<Either<BaseError, Unit>> WaitForReady(
        string channelNumber,
        IHlsSessionWorker worker,
        int initialSegmentCount,
        TimeSpan startDeadline,
        CancellationToken cancellationToken);
    bool TryAddWorker(string channelNumber, IHlsSessionWorker worker);
    void RemoveWorker(string channelNumber, IHlsSessionWorker worker);
    bool IsActive(string channelNumber);
    Task<bool> StopChannel(string channelNumber, CancellationToken cancellationToken);
    void TouchChannel(string channelNumber, string fileName);
    void PlayoutUpdated(string channelNumber);
}
