using System.Collections.Concurrent;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next.Description;
using LanguageExt;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Tests.Streaming;

[TestFixture]
public class NextProbeServiceTests
{
    private sealed class FakeProbeService() : NextProbeService(new MockFileSystem(), NullLogger<NextProbeService>.Instance)
    {
        private readonly ConcurrentQueue<TaskCompletionSource<Option<(RuntimeCapability, string)>>> _pending = new();

        public int Calls => _calls;
        private int _calls;

        public TaskCompletionSource<Option<(RuntimeCapability, string)>> NextProbe()
        {
            SpinWait.SpinUntil(() => !_pending.IsEmpty, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            _pending.TryDequeue(out TaskCompletionSource<Option<(RuntimeCapability, string)>>? tcs).ShouldBeTrue();
            return tcs!;
        }

        protected override Task<Option<(RuntimeCapability Capability, string Json)>> Probe(string ffmpegPath)
        {
            Interlocked.Increment(ref _calls);
            var tcs = new TaskCompletionSource<Option<(RuntimeCapability, string)>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(tcs);
            return tcs.Task;
        }
    }

    private static (RuntimeCapability, string) Capability(string ffmpegPath) =>
        (new RuntimeCapability { Accels = [], Ffmpeg = new FfmpegRuntime { Path = ffmpegPath, Hwaccels = [] } }, "{}");

    [Test]
    public async Task Concurrent_Refreshes_Share_One_Probe()
    {
        var service = new FakeProbeService();

        Task<Option<NextProbeResult>> first = service.Refresh("ffmpeg", CancellationToken.None);
        Task<Option<NextProbeResult>> second = service.Refresh("ffmpeg", CancellationToken.None);
        service.NextProbe().SetResult(Capability("ffmpeg"));

        (await first).IsSome.ShouldBeTrue();
        (await second).IsSome.ShouldBeTrue();
        service.Calls.ShouldBe(1);
    }

    [Test]
    public async Task Refresh_After_Completion_Probes_Again()
    {
        var service = new FakeProbeService();

        Task<Option<NextProbeResult>> first = service.Refresh("ffmpeg", CancellationToken.None);
        service.NextProbe().SetResult(Capability("ffmpeg"));
        await first;

        Task<Option<NextProbeResult>> second = service.Refresh("ffmpeg", CancellationToken.None);
        service.NextProbe().SetResult(Capability("ffmpeg"));
        await second;

        service.Calls.ShouldBe(2);
    }

    [Test]
    public async Task Failed_Refresh_Keeps_Last_Good_Result()
    {
        var service = new FakeProbeService();
        (RuntimeCapability, string) good = Capability("ffmpeg");

        Task<Option<NextProbeResult>> first = service.Refresh("ffmpeg", CancellationToken.None);
        service.NextProbe().SetResult(good);
        await first;

        Task<Option<NextProbeResult>> second = service.Refresh("ffmpeg", CancellationToken.None);
        service.NextProbe().SetException(new InvalidOperationException("hung"));
        Option<NextProbeResult> result = await second;

        result.Map(r => r.Capability).ShouldBe(Option<RuntimeCapability>.Some(good.Item1));
        service.Latest.Map(r => r.Capability).ShouldBe(Option<RuntimeCapability>.Some(good.Item1));
    }

    [Test]
    public async Task Older_Path_Finishing_Last_Does_Not_Replace_Newer()
    {
        var service = new FakeProbeService();

        Task<Option<NextProbeResult>> oldPath = service.Refresh("old", CancellationToken.None);
        TaskCompletionSource<Option<(RuntimeCapability, string)>> oldProbe = service.NextProbe();
        Task<Option<NextProbeResult>> newPath = service.Refresh("new", CancellationToken.None);
        TaskCompletionSource<Option<(RuntimeCapability, string)>> newProbe = service.NextProbe();

        newProbe.SetResult(Capability("new"));
        await newPath;
        oldProbe.SetResult(Capability("old"));
        await oldPath;

        service.Latest.Map(r => r.FFmpegPath).ShouldBe(Option<string>.Some("new"));
    }

    [Test]
    public async Task Cancelled_Caller_Does_Not_Cancel_Shared_Probe()
    {
        var service = new FakeProbeService();
        using var cts = new CancellationTokenSource();

        Task<Option<NextProbeResult>> cancelled = service.Refresh("ffmpeg", cts.Token);
        Task<Option<NextProbeResult>> other = service.Refresh("ffmpeg", CancellationToken.None);
        TaskCompletionSource<Option<(RuntimeCapability, string)>> probe = service.NextProbe();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(cancelled);

        probe.SetResult(Capability("ffmpeg"));
        (await other).IsSome.ShouldBeTrue();
    }
}
