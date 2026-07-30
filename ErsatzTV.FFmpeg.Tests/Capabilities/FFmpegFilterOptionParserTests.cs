using System.Collections.Generic;
using ErsatzTV.FFmpeg.Capabilities;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.FFmpeg.Tests.Capabilities;

[TestFixture]
public class FFmpegFilterOptionParserTests
{
    // stock ffmpeg vpp_qsv, without the pad_w/pad_h/pad_x/pad_y/pad_color options
    private const string WithoutPadOptions = @"Filter vpp_qsv
  Quick Sync Video VPP.
    Inputs:
       #0: default (video)
    Outputs:
       #0: default (video)
vpp_qsv AVOptions:
   deinterlace       <int>        ..FV....... deinterlace mode (from 0 to 2) (default 0)
     bob             1            ..FV....... Get one frame for each field
     advanced        2            ..FV....... Get one frame for each frame
   denoise           <int>        ..FV....... denoise level [0, 100] (from 0 to 100) (default 0)
   framerate         <rational>   ..FV....... output framerate (from 0 to DBL_MAX) (default 0/1)
   w                 <string>     ..FV....... Output video width
   h                 <string>     ..FV....... Output video height
   format            <string>     ..FV....... Output pixel format
   async_depth       <int>        ..FV....... Internal parallelization depth (from 0 to INT_MAX) (default 4)
   scale_mode        <int>        ..FV....... scaling mode (from 0 to 2) (default 0)
";

    // patched ffmpeg vpp_qsv, carrying the pad_w/pad_h/pad_x/pad_y/pad_color options
    private const string WithPadOptions = @"Filter vpp_qsv
  Quick Sync Video VPP.
    Inputs:
       #0: default (video)
    Outputs:
       #0: default (video)
vpp_qsv AVOptions:
   deinterlace       <int>        ..FV....... deinterlace mode (from 0 to 2) (default 0)
     bob             1            ..FV....... Get one frame for each field
     advanced        2            ..FV....... Get one frame for each frame
   denoise           <int>        ..FV....... denoise level [0, 100] (from 0 to 100) (default 0)
   framerate         <rational>   ..FV....... output framerate (from 0 to DBL_MAX) (default 0/1)
   w                 <string>     ..FV....... Output video width
   h                 <string>     ..FV....... Output video height
   format            <string>     ..FV....... Output pixel format
   async_depth       <int>        ..FV....... Internal parallelization depth (from 0 to INT_MAX) (default 4)
   scale_mode        <int>        ..FV....... scaling mode (from 0 to 2) (default 0)
   pad_w             <int>        ..FV....... Padded width (from 0 to 8192) (default 0)
   pad_h             <int>        ..FV....... Padded height (from 0 to 8192) (default 0)
   pad_x             <int>        ..FV....... Horizontal position (from -1 to 8192) (default -1)
   pad_y             <int>        ..FV....... Vertical position (from -1 to 8192) (default -1)
   pad_color         <color>      ..FV....... Padding color (default ""black"")
";

    [Test]
    public void Should_Parse_Option_Names()
    {
        IReadOnlySet<string> options = FFmpegFilterOptionParser.Parse(WithoutPadOptions);

        options.ShouldContain("deinterlace");
        options.ShouldContain("denoise");
        options.ShouldContain("framerate");
        options.ShouldContain("w");
        options.ShouldContain("h");
        options.ShouldContain("format");
    }

    [Test]
    public void Should_Not_Parse_Enum_Constants()
    {
        IReadOnlySet<string> options = FFmpegFilterOptionParser.Parse(WithoutPadOptions);

        options.ShouldNotContain("bob");
        options.ShouldNotContain("advanced");
    }

    [Test]
    public void Should_Detect_Pad_Options_When_Present()
    {
        IReadOnlySet<string> options = FFmpegFilterOptionParser.Parse(WithPadOptions);

        options.ShouldContain("pad_w");
        options.ShouldContain("pad_h");
        options.ShouldContain("pad_x");
        options.ShouldContain("pad_y");
        options.ShouldContain("pad_color");
    }

    [Test]
    public void Should_Not_Detect_Pad_Options_When_Absent()
    {
        IReadOnlySet<string> options = FFmpegFilterOptionParser.Parse(WithoutPadOptions);

        options.ShouldNotContain("pad_w");
        options.ShouldNotContain("pad_h");
        options.ShouldNotContain("pad_color");
    }
}
