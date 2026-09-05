using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Core.FFmpeg;

public static class SessionStartCoordinator
{
    public static async Task<Either<BaseError, Unit>> Start(
        IFFmpegSegmenterService service,
        string channelNumber,
        Func<Task<Either<BaseError, IHlsSessionWorker>>> createWorker,
        int initialSegmentCount,
        TimeSpan startDeadline,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            IHlsSessionWorker worker;
            bool existing;
            using (await service.LockForStart(channelNumber, cancellationToken))
            {
                existing = service.TryGetWorker(channelNumber, out worker);
                if (existing)
                {
                    worker.Touch(Option<string>.None);
                }
                else
                {
                    Either<BaseError, IHlsSessionWorker> created = await createWorker();
                    if (created.IsLeft)
                    {
                        return created.LeftToSeq().Head();
                    }

                    worker = created.RightToSeq().Head();
                }
            }

            // A slow startup must not queue every viewer behind a separate readiness deadline.
            Either<BaseError, Unit> ready = await service.WaitForReady(
                channelNumber, worker, initialSegmentCount, startDeadline, cancellationToken);
            if (attempt == 0 && existing && ready.IsLeft &&
                ready.LeftToSeq().Head() is SessionEndedBeforeReady)
            {
                // Reacquire the lock and check for another viewer's replacement before creating one.
                continue;
            }

            return ready;
        }
    }
}
