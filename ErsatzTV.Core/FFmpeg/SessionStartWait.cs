using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Core.FFmpeg;

public static class SessionStartWait
{
    public static async Task<Either<BaseError, Unit>> ForReady(
        string channelNumber,
        IHlsSessionWorker worker,
        Task runTask,
        int initialSegmentCount,
        TimeSpan startDeadline,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(startDeadline);

        Task waitTask = worker.WaitForPlaylistSegments(initialSegmentCount, timeout.Token);

        try
        {
            await Task.WhenAny(waitTask, runTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (runTask.IsCompleted)
            {
                return new SessionEndedBeforeReady(channelNumber);
            }

            await waitTask;
            timeout.Token.ThrowIfCancellationRequested();
            if (runTask.IsCompleted)
            {
                return new SessionEndedBeforeReady(channelNumber);
            }

            return Unit.Default;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BaseError.New($"Session for channel {channelNumber} did not become ready in {startDeadline}");
        }
        finally
        {
            // the wait polls on a timer; disposing the token source does not stop it, so an
            // abandoned wait would poll for the life of the process
            await timeout.CancelAsync();

            try
            {
                await waitTask;
            }
            catch (Exception)
            {
                // the start already has its result
            }
        }
    }
}
