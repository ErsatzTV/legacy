using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class SessionStartCoordinatorTests
{
    private FFmpegSegmenterService _service;
    private int _created;

    [SetUp]
    public void SetUp()
    {
        _service = new FFmpegSegmenterService(NullLogger<FFmpegSegmenterService>.Instance);
        _created = 0;
    }

    [Test]
    public async Task Viewers_Should_Wait_Concurrently_And_Create_Only_One_Worker()
    {
        var playlist = new TaskCompletionSource();
        IHlsSessionWorker worker = WaitingWorker(playlist.Task);
        Task<Either<BaseError, Unit>> first = Start(worker);
        Task<Either<BaseError, Unit>> second = Start(worker);

        // Both requests must reach the readiness wait before either playlist wait completes.
        worker.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(worker.WaitForPlaylistSegments))
            .ShouldBe(2);
        _created.ShouldBe(1);
        first.IsCompleted.ShouldBeFalse();
        second.IsCompleted.ShouldBeFalse();

        playlist.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
    }

    [Test]
    public async Task Existing_Worker_Ending_Should_Start_One_Replacement()
    {
        IHlsSessionWorker existing = WaitingWorker(new TaskCompletionSource().Task);
        _service.TryAddWorker("1", existing);
        IHlsSessionWorker replacement = WaitingWorker(Task.CompletedTask);
        Task<Either<BaseError, Unit>> request = Start(replacement);

        _service.RemoveWorker("1", existing);

        (await request.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
        _created.ShouldBe(1);
        _service.TryGetWorker("1", out IHlsSessionWorker current).ShouldBeTrue();
        current.ShouldBeSameAs(replacement);
    }

    [Test]
    public async Task Recovery_Should_Use_Another_Viewers_Replacement()
    {
        IHlsSessionWorker existing = WaitingWorker(new TaskCompletionSource().Task);
        _service.TryAddWorker("1", existing);
        IHlsSessionWorker replacement = WaitingWorker(Task.CompletedTask);
        Task<Either<BaseError, Unit>> request = Start(replacement);

        using (await _service.LockForStart("1", CancellationToken.None))
        {
            _service.RemoveWorker("1", existing);
            _service.TryAddWorker("1", replacement);
        }

        (await request.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
        _created.ShouldBe(0);
    }

    [Test]
    public async Task Replacement_Ending_Should_Not_Cause_An_Unbounded_Restart_Loop()
    {
        IHlsSessionWorker existing = WaitingWorker(new TaskCompletionSource().Task);
        _service.TryAddWorker("1", existing);
        var replacementWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IHlsSessionWorker replacement = WaitingWorker(new TaskCompletionSource().Task);
        replacement.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                replacementWait.TrySetResult();
                return Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            });
        Task<Either<BaseError, Unit>> request = Start(replacement);
        _service.RemoveWorker("1", existing);
        await replacementWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _service.RemoveWorker("1", replacement);

        (await request.WaitAsync(TimeSpan.FromSeconds(5))).LeftToSeq().Head()
            .ShouldBeOfType<SessionEndedBeforeReady>();
        _created.ShouldBe(1);
    }

    [Test]
    public async Task Deadline_Should_Not_Restart_A_Running_Worker()
    {
        IHlsSessionWorker existing = WaitingWorker(new TaskCompletionSource().Task);
        _service.TryAddWorker("1", existing);

        Either<BaseError, Unit> result = await Start(existing, deadline: TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.IsLeft.ShouldBeTrue();
        result.LeftToSeq().Head().ShouldNotBeOfType<SessionEndedBeforeReady>();
        _created.ShouldBe(0);
        _service.IsActive("1").ShouldBeTrue();
    }

    [Test]
    public async Task Canceling_One_Viewer_Should_Not_Cancel_Another_Viewers_Wait()
    {
        var playlist = new TaskCompletionSource();
        IHlsSessionWorker worker = WaitingWorker(playlist.Task);
        using var cts = new CancellationTokenSource();
        Task<Either<BaseError, Unit>> first = Start(worker, cts.Token);
        Task<Either<BaseError, Unit>> second = Start(worker);

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        second.IsCompleted.ShouldBeFalse();
        playlist.SetResult();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();
        _created.ShouldBe(1);
    }

    private Task<Either<BaseError, Unit>> Start(
        IHlsSessionWorker worker,
        CancellationToken cancellationToken = default,
        TimeSpan? deadline = null) =>
        SessionStartCoordinator.Start(
            _service,
            "1",
            () =>
            {
                _created++;
                _service.TryAddWorker("1", worker).ShouldBeTrue();
                return Task.FromResult(Right<BaseError, IHlsSessionWorker>(worker));
            },
            1,
            deadline ?? TimeSpan.FromSeconds(5),
            cancellationToken);

    private static IHlsSessionWorker WaitingWorker(Task playlist)
    {
        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();
        worker.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => playlist.WaitAsync(call.Arg<CancellationToken>()));
        return worker;
    }
}
