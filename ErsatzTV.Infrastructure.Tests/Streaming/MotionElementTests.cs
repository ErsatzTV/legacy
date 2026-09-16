using System.ComponentModel;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Streaming.Graphics;
using LanguageExt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Tests.Streaming;

[TestFixture]
public class MotionElementTests
{
    private string _folder = null!;
    private string _videoPath = null!;

    [OneTimeSetUp]
    public async Task CreateAnimation()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"etv-motion-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(FileSystemLayout.TempFilePoolFolder);
        _videoPath = Path.Combine(_folder, "animation.mov");
        try
        {
            // One second red, then one second blue, with an alpha-capable codec.
            await Cli.Wrap("ffmpeg").WithArguments(new[]
            {
                "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=red:s=16x16:r=10:d=1",
                "-f", "lavfi", "-i", "color=blue:s=16x16:r=10:d=1",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0",
                "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", _videoPath
            }).ExecuteAsync();
        }
        catch (Win32Exception)
        {
            Assert.Ignore("Motion integration tests require ffmpeg on PATH.");
        }
    }

    [OneTimeTearDown]
    public void RemoveAnimation() => Directory.Delete(_folder, true);

    [TestCase(0, 5, false)]
    [TestCase(5, 5, false)]
    [TestCase(5.5, 5.5, false)]
    [TestCase(6.5, 6.5, true)]
    [TestCase(6.95, 6.95, true)]
    [TestCase(6.99, 6.99, true)]
    [TestCase(7, 7, true)]
    [TestCase(600, 600, true)]
    public async Task Hold_should_show_the_correct_frame_when_joining(double seek, double time, bool blue)
    {
        using var element = await CreateElement(MotionEndBehavior.Hold, seek);
        await AssertFrame(element, time, blue);
        (await Prepare(element, 3607)).IsNone.ShouldBeTrue();
        element.IsFinished.ShouldBeTrue();
    }

    [Test]
    public async Task Hold_should_resume_mid_clip_then_retain_the_final_frame()
    {
        using var element = await CreateElement(MotionEndBehavior.Hold, 5.5);
        for (var frame = 0; frame < 20; frame++)
        {
            await AssertFrame(element, 5.5 + frame / 10.0, frame >= 5);
        }

        await AssertFrame(element, 3606.9, true);
        (await Prepare(element, 3607)).IsNone.ShouldBeTrue();
    }

    [Test]
    public async Task Hold_should_play_then_retain_the_final_frame()
    {
        using var element = await CreateElement(MotionEndBehavior.Hold, 0);
        (await Prepare(element, 4.9)).IsNone.ShouldBeTrue();
        for (var frame = 0; frame < 25; frame++)
        {
            await AssertFrame(element, 5 + frame / 10.0, frame >= 10);
        }
    }

    [TestCase(MotionEndBehavior.Hold, 3607)]
    [TestCase(MotionEndBehavior.Hold, 3608)]
    [TestCase(MotionEndBehavior.Disappear, 7)]
    public async Task Should_be_finished_when_joining_at_or_after_end(MotionEndBehavior behavior, double seek)
    {
        using var element = await CreateElement(behavior, seek);
        element.IsFinished.ShouldBeTrue();
        (await Prepare(element, seek)).IsNone.ShouldBeTrue();
    }

    [TestCase(MotionEndBehavior.Hold)]
    [TestCase(MotionEndBehavior.Disappear)]
    public async Task Should_enforce_end_time_even_while_playing(MotionEndBehavior behavior)
    {
        using var element = await CreateElement(behavior, 5, holdSeconds: 0);
        await AssertFrame(element, 5, false);
        (await Prepare(element, 7)).IsNone.ShouldBeTrue();
        element.IsFinished.ShouldBeTrue();
    }

    [TestCase(600, true)]
    [TestCase(601, false)]
    public async Task Loop_should_wrap_seek_and_continue_across_the_source_end(double seek, bool blue)
    {
        using var element = await CreateElement(MotionEndBehavior.Loop, seek);
        for (var frame = 0; frame < 25; frame++)
        {
            bool expectedBlue = ((blue ? 10 : 0) + frame) % 20 >= 10;
            await AssertFrame(element, seek + frame / 10.0, expectedBlue);
        }
    }

    [Test]
    public async Task Loop_should_end_at_the_stream_end_without_adding_start_seconds()
    {
        using var element = await CreateElement(MotionEndBehavior.Loop, 0, duration: 8);
        await AssertFrame(element, 5, false);
        (await Prepare(element, 8)).IsNone.ShouldBeTrue();
        element.IsFinished.ShouldBeTrue();
    }

    [Test]
    public async Task Should_skip_elements_starting_after_the_stream()
    {
        using var element = await CreateElement(MotionEndBehavior.Loop, 0, duration: 5);
        element.IsFinished.ShouldBeTrue();
    }

    private async Task<MotionElement> CreateElement(
        MotionEndBehavior behavior, double seek, double holdSeconds = 3600, double duration = 4000)
    {
        var statistics = Substitute.For<ILocalStatisticsProvider>();
        statistics.GetStatistics(Arg.Any<string>(), _videoPath).Returns(
            Task.FromResult<Either<BaseError, MediaVersion>>(new MediaVersion
            {
                Width = 16, Height = 16, Duration = TimeSpan.FromSeconds(2), Streams = []
            }));
        var element = new MotionElement(new MotionGraphicsElement
        {
            VideoPath = _videoPath, StartSeconds = 5, EndBehavior = behavior, HoldSeconds = holdSeconds
        }, "ffprobe", "ffmpeg", statistics, NullLogger.Instance);
        var size = new Resolution { Width = 16, Height = 16 };
        await element.InitializeAsync(new GraphicsEngineContext(
            "1", new Movie(), [], [], size, size, new FrameRate("10"),
            DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now,
            TimeSpan.FromSeconds(seek), TimeSpan.FromSeconds(duration), TimeSpan.FromSeconds(seek + duration)),
            CancellationToken.None);
        return element;
    }

    private static async Task AssertFrame(MotionElement element, double time, bool blue)
    {
        Option<PreparedElementImage> image = await Prepare(element, time);
        image.IsSome.ShouldBeTrue();
        foreach (PreparedElementImage frame in image)
        {
            SKColor pixel = frame.Image.GetPixel(8, 8);
            (blue ? pixel.Blue : pixel.Red).ShouldBeGreaterThan((byte)200);
            (blue ? pixel.Red : pixel.Blue).ShouldBeLessThan((byte)30);
            pixel.Alpha.ShouldBe((byte)255);
        }
    }

    private static async Task<Option<PreparedElementImage>> Prepare(MotionElement element, double time)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await element.PrepareImage(TimeSpan.Zero, TimeSpan.FromSeconds(time), TimeSpan.Zero,
            TimeSpan.Zero, timeout.Token);
    }
}
