using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class SessionStartWaitTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(250);

    [Test]
    public async Task ForReady_Should_Succeed_When_The_Playlist_Becomes_Ready()
    {
        var wait = new TaskCompletionSource();
        IHlsSessionWorker worker = WorkerWaiting(wait.Task, out _);

        var run = new TaskCompletionSource();
        Task<Either<BaseError, Unit>> ready = SessionStartWait.ForReady(
            "1",
            worker,
            run.Task,
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        wait.SetResult();

        (await ready.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
    }

    [Test]
    public async Task ForReady_Should_Fail_When_The_Run_Task_Ends_First()
    {
        IHlsSessionWorker worker = WorkerWaiting(NeverReady(), out _);

        var run = new TaskCompletionSource();
        Task<Either<BaseError, Unit>> ready = SessionStartWait.ForReady(
            "1",
            worker,
            run.Task,
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        run.SetResult();

        Either<BaseError, Unit> result = await ready.WaitAsync(TimeSpan.FromSeconds(5));
        ErrorMessage(result).ShouldContain("ended before the playlist was ready");
    }

    [Test]
    public async Task ForReady_Should_Stop_The_Wait_When_The_Run_Task_Ends_First()
    {
        IHlsSessionWorker worker = WorkerWaiting(NeverReady(), out Func<CancellationToken> waitToken);

        var run = new TaskCompletionSource();
        Task<Either<BaseError, Unit>> ready = SessionStartWait.ForReady(
            "1",
            worker,
            run.Task,
            1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        run.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(5));

        // an abandoned wait polls on a timer, so it has to be cancelled, not just left behind
        waitToken().IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task ForReady_Should_Fail_At_The_Deadline_When_The_Playlist_Never_Appears()
    {
        IHlsSessionWorker worker = WorkerWaiting(NeverReady(), out _);

        Either<BaseError, Unit> result = await SessionStartWait.ForReady(
                "1",
                worker,
                NeverReady(),
                1,
                ShortDeadline,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        ErrorMessage(result).ShouldContain("did not become ready");
    }

    [Test]
    public async Task ForReady_Should_Propagate_Cancellation_From_The_Request()
    {
        IHlsSessionWorker worker = WorkerWaiting(NeverReady(), out _);

        using var cts = new CancellationTokenSource();
        Task<Either<BaseError, Unit>> ready = SessionStartWait.ForReady(
            "1",
            worker,
            NeverReady(),
            1,
            TimeSpan.FromSeconds(30),
            cts.Token);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => ready.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static Task NeverReady() => new TaskCompletionSource().Task;

    private static string ErrorMessage(Either<BaseError, Unit> result) =>
        result.Match(_ => string.Empty, error => error.Value);

    // returns a worker whose wait completes with `wait`, plus an accessor for the token it was given
    private static IHlsSessionWorker WorkerWaiting(Task wait, out Func<CancellationToken> waitToken)
    {
        CancellationToken captured = CancellationToken.None;
        waitToken = () => captured;

        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();
        worker.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<CancellationToken>();
                return wait.WaitAsync(captured);
            });

        return worker;
    }
}
