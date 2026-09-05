using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.Filter.Qsv;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.State;
using LanguageExt;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.FFmpeg.Tests.Filter.Qsv;

[TestFixture]
public class PadQsvFilterTests
{
    private static FrameState FrameStateAt(FrameDataLocation location, Option<IPixelFormat> pixelFormat) => new(
        false,
        false,
        string.Empty,
        Option<string>.None,
        Option<string>.None,
        true,
        pixelFormat,
        new FrameSize(1920, 1080),
        new FrameSize(1920, 1080),
        Option<FrameSize>.None,
        FFmpegFilterMode.HardwareIfPossible,
        false,
        Option<FrameRate>.None,
        Option<int>.None,
        Option<int>.None,
        Option<int>.None,
        false,
        false,
        location);

    [Test]
    public void Should_Emit_Hardware_Pad_When_Frame_In_Hardware()
    {
        var filter = new PadQsvFilter(
            FrameStateAt(FrameDataLocation.Hardware, Option<IPixelFormat>.None),
            new FrameSize(1920, 1080),
            64);

        filter.Filter.ShouldBe("vpp_qsv=pad_w=1920:pad_h=1080:pad_x=-1:pad_y=-1:pad_color=black");
    }

    [Test]
    public void Should_Upload_Before_Pad_When_Frame_In_Software()
    {
        var filter = new PadQsvFilter(
            FrameStateAt(FrameDataLocation.Software, Option<IPixelFormat>.Some(new PixelFormatNv12("yuv420p"))),
            new FrameSize(1920, 1080),
            64);

        filter.Filter.ShouldBe(
            "format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=pad_w=1920:pad_h=1080:pad_x=-1:pad_y=-1:pad_color=black");
    }

    [Test]
    public void Should_Report_Padded_Size_And_Hardware_Location()
    {
        var filter = new PadQsvFilter(
            FrameStateAt(FrameDataLocation.Software, Option<IPixelFormat>.None),
            new FrameSize(1920, 1080),
            64);

        FrameState nextState = filter.NextState(
            FrameStateAt(FrameDataLocation.Software, Option<IPixelFormat>.None));

        nextState.PaddedSize.ShouldBe(new FrameSize(1920, 1080));
        nextState.FrameDataLocation.ShouldBe(FrameDataLocation.Hardware);
    }
}
