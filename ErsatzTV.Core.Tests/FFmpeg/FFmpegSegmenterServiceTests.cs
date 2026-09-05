using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class FFmpegSegmenterServiceTests
{
    [SetUp]
    public void SetUp() => _service = new FFmpegSegmenterService(NullLogger<FFmpegSegmenterService>.Instance);

    private FFmpegSegmenterService _service;

    [Test]
    public void TryAddWorker_Should_Fail_For_Second_Worker_On_Same_Channel()
    {
        IHlsSessionWorker first = Substitute.For<IHlsSessionWorker>();
        IHlsSessionWorker second = Substitute.For<IHlsSessionWorker>();

        _service.TryAddWorker("1", first).ShouldBeTrue();
        _service.TryAddWorker("1", second).ShouldBeFalse();

        _service.TryGetWorker("1", out IHlsSessionWorker actual).ShouldBeTrue();
        actual.ShouldBeSameAs(first);
    }

    [Test]
    public void TryAddWorker_Should_Throw_For_Null_Worker() =>
        Should.Throw<ArgumentNullException>(() => _service.TryAddWorker("1", null));

    [Test]
    public void TryAddWorker_Should_Not_Mark_Channel_Active_When_It_Throws()
    {
        Should.Throw<ArgumentNullException>(() => _service.TryAddWorker("1", null));

        _service.IsActive("1").ShouldBeFalse();
    }

    [Test]
    public void RemoveWorker_Should_Ignore_A_Worker_That_Is_Not_Registered()
    {
        IHlsSessionWorker current = Substitute.For<IHlsSessionWorker>();
        IHlsSessionWorker stale = Substitute.For<IHlsSessionWorker>();

        _service.TryAddWorker("1", current).ShouldBeTrue();
        _service.RemoveWorker("1", stale);

        _service.IsActive("1").ShouldBeTrue();
        _service.TryGetWorker("1", out IHlsSessionWorker actual).ShouldBeTrue();
        actual.ShouldBeSameAs(current);
    }

    [Test]
    public void RemoveWorker_Should_Remove_Its_Own_Worker()
    {
        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();

        _service.TryAddWorker("1", worker).ShouldBeTrue();
        _service.RemoveWorker("1", worker);

        _service.IsActive("1").ShouldBeFalse();
        _service.Workers.ShouldBeEmpty();
    }

    [Test]
    public void OnWorkersChanged_Should_Only_Fire_For_Effective_Changes()
    {
        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();
        IHlsSessionWorker other = Substitute.For<IHlsSessionWorker>();

        var count = 0;
        _service.OnWorkersChanged += (_, _) => count++;

        _service.TryAddWorker("1", worker);
        count.ShouldBe(1);

        _service.TryAddWorker("1", other);
        count.ShouldBe(1);

        _service.RemoveWorker("1", other);
        count.ShouldBe(1);

        _service.RemoveWorker("1", worker);
        count.ShouldBe(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WaitForReady_Should_Wait_Again_After_Interrupted_Startup(bool cancelRequest)
    {
        var playlist = new TaskCompletionSource();
        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();
        worker.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => playlist.Task.WaitAsync(call.Arg<CancellationToken>()));
        _service.TryAddWorker("1", worker);

        using var cts = new CancellationTokenSource();
        Task<Either<BaseError, Unit>> first = _service.WaitForReady(
            "1", worker, 1,
            cancelRequest ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50), cts.Token);
        if (cancelRequest)
        {
            await cts.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => first);
        }
        else
        {
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).IsLeft.ShouldBeTrue();
        }

        _service.TryGetWorker("1", out IHlsSessionWorker existing).ShouldBeTrue();
        Task<Either<BaseError, Unit>> retry = _service.WaitForReady(
            "1", existing, 1, TimeSpan.FromSeconds(5), CancellationToken.None);
        retry.IsCompleted.ShouldBeFalse();

        playlist.SetResult();
        (await retry.WaitAsync(TimeSpan.FromSeconds(5))).IsRight.ShouldBeTrue();

        // Established sessions should not poll the playlist again on every tune-in.
        worker.ClearReceivedCalls();
        (await _service.WaitForReady("1", worker, 1, TimeSpan.FromSeconds(5), CancellationToken.None))
            .IsRight.ShouldBeTrue();
        await worker.DidNotReceive().WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WaitForReady_Should_Fail_When_Worker_Is_Removed_And_Not_Reuse_Readiness()
    {
        var playlist = new TaskCompletionSource();
        IHlsSessionWorker worker = Substitute.For<IHlsSessionWorker>();
        worker.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => playlist.Task.WaitAsync(call.Arg<CancellationToken>()));
        _service.TryAddWorker("1", worker);
        Task<Either<BaseError, Unit>> waiting = _service.WaitForReady(
            "1", worker, 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        _service.RemoveWorker("1", worker);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).IsLeft.ShouldBeTrue();

        IHlsSessionWorker replacement = Substitute.For<IHlsSessionWorker>();
        replacement.WaitForPlaylistSegments(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _service.TryAddWorker("1", replacement);
        (await _service.WaitForReady("1", replacement, 1, TimeSpan.FromSeconds(5), CancellationToken.None))
            .IsRight.ShouldBeTrue();
        (await _service.WaitForReady("1", worker, 1, TimeSpan.FromSeconds(5), CancellationToken.None))
            .IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task LockForStart_Should_Block_A_Second_Start_On_The_Same_Channel()
    {
        IDisposable first = await _service.LockForStart("1", CancellationToken.None);

        Task<IDisposable> second = _service.LockForStart("1", CancellationToken.None);
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(250)))).ShouldNotBe(second);

        first.Dispose();

        IDisposable acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.Dispose();
    }

    [Test]
    public async Task LockForStart_Should_Not_Block_A_Start_On_Another_Channel()
    {
        IDisposable first = await _service.LockForStart("1", CancellationToken.None);

        IDisposable second = await _service.LockForStart("2", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        second.Dispose();
        first.Dispose();
    }

    [Test]
    public async Task LockForStart_Should_Release_When_The_Releaser_Is_Disposed_Twice()
    {
        IDisposable first = await _service.LockForStart("1", CancellationToken.None);
        first.Dispose();
        first.Dispose();

        IDisposable second = await _service.LockForStart("1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // a double dispose must not leave 2 permits behind
        Task<IDisposable> third = _service.LockForStart("1", CancellationToken.None);
        (await Task.WhenAny(third, Task.Delay(TimeSpan.FromMilliseconds(250)))).ShouldNotBe(third);

        second.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Test]
    public async Task LockForStart_Should_Throw_When_The_Token_Is_Already_Cancelled()
    {
        IDisposable first = await _service.LockForStart("1", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _service.LockForStart("1", cts.Token));

        // the cancelled waiter must not have taken the lock
        first.Dispose();
        (await _service.LockForStart("1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
