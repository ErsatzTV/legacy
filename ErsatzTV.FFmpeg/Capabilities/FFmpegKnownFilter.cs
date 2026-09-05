namespace ErsatzTV.FFmpeg.Capabilities;

public record FFmpegKnownFilter
{
    public static readonly FFmpegKnownFilter ScaleNpp = new("scale_npp");
    public static readonly FFmpegKnownFilter TonemapOpenCL = new("tonemap_opencl");
    //public static readonly FFmpegKnownFilter Libplacebo = new("libplacebo");
    public static readonly FFmpegKnownFilter AudioPad = new("apad");
    public static readonly FFmpegKnownFilter AudioResample = new("aresample");
    public static readonly FFmpegKnownFilter Color = new("color");
    public static readonly FFmpegKnownFilter ColorChannelMixer = new("colorchannelmixer");
    public static readonly FFmpegKnownFilter Colorspace = new("colorspace");
    public static readonly FFmpegKnownFilter Crop = new("crop");
    public static readonly FFmpegKnownFilter Fade = new("fade");
    public static readonly FFmpegKnownFilter Format = new("format");
    public static readonly FFmpegKnownFilter Fps = new("fps");
    public static readonly FFmpegKnownFilter FrameRate = new("framerate");
    public static readonly FFmpegKnownFilter Loop = new("loop");
    public static readonly FFmpegKnownFilter NormalizeLoudness = new("loudnorm");
    public static readonly FFmpegKnownFilter Overlay = new("overlay");
    public static readonly FFmpegKnownFilter Pad = new("pad");
    public static readonly FFmpegKnownFilter Realtime = new("realtime");
    public static readonly FFmpegKnownFilter SetPts = new("setpts");
    public static readonly FFmpegKnownFilter Scale = new("scale");
    public static readonly FFmpegKnownFilter Subtitles = new("subtitles");
    public static readonly FFmpegKnownFilter Tonemap = new("tonemap");
    public static readonly FFmpegKnownFilter VppQsv = new("vpp_qsv");
    public static readonly FFmpegKnownFilter Yadif = new("yadif");
    public static readonly FFmpegKnownFilter ZScale = new("zscale");

    private FFmpegKnownFilter(string Name) => this.Name = Name;

    public string Name { get; }

    public static IList<string> AllFilters =>
    [
        ScaleNpp.Name,
        TonemapOpenCL.Name,
    ];

    public static IList<FFmpegKnownFilter> RequiredFilters =>
    [
        AudioPad,
        AudioResample,
        Color,
        ColorChannelMixer,
        Colorspace,
        Crop,
        Fade,
        Format,
        Fps,
        FrameRate,
        Loop,
        NormalizeLoudness,
        Overlay,
        Pad,
        Realtime,
        SetPts,
        Scale,
        Subtitles,
        Tonemap,
        Yadif,
        ZScale
    ];
}
