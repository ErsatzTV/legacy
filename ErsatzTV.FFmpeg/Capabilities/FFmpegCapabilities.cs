using ErsatzTV.FFmpeg.Decoder;
using ErsatzTV.FFmpeg.Format;

namespace ErsatzTV.FFmpeg.Capabilities;

public class FFmpegCapabilities(
    string ffmpegVersion,
    IReadOnlySet<string> ffmpegHardwareAccelerations,
    IReadOnlySet<string> ffmpegDecoders,
    IReadOnlySet<string> ffmpegFilters,
    IReadOnlySet<string> ffmpegEncoders,
    IReadOnlySet<string> ffmpegOptions,
    IReadOnlySet<string> ffmpegDemuxFormats,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ffmpegFilterOptions)
    : IFFmpegCapabilities
{
    public string Version => ffmpegVersion;

    public bool HasHardwareAcceleration(HardwareAccelerationMode hardwareAccelerationMode)
    {
        // AMF isn't a "hwaccel" in ffmpeg, so check for presence of encoders
        if (hardwareAccelerationMode is HardwareAccelerationMode.Amf)
        {
            return ffmpegEncoders.Any(e => e.EndsWith(
                $"_{FFmpegKnownHardwareAcceleration.Amf.Name}",
                StringComparison.OrdinalIgnoreCase));
        }

        // V4l2m2m isn't a "hwaccel" in ffmpeg, so check for presence of encoders
        if (hardwareAccelerationMode is HardwareAccelerationMode.V4l2m2m)
        {
            return ffmpegEncoders.Any(e => e.EndsWith(
                $"_{FFmpegKnownHardwareAcceleration.V4l2m2m.Name}",
                StringComparison.OrdinalIgnoreCase));
        }

        Option<FFmpegKnownHardwareAcceleration> maybeAccelToCheck = hardwareAccelerationMode switch
        {
            HardwareAccelerationMode.Nvenc => FFmpegKnownHardwareAcceleration.Cuda,
            HardwareAccelerationMode.Qsv => FFmpegKnownHardwareAcceleration.Qsv,
            HardwareAccelerationMode.Vaapi => FFmpegKnownHardwareAcceleration.Vaapi,
            HardwareAccelerationMode.VideoToolbox => FFmpegKnownHardwareAcceleration.VideoToolbox,
            HardwareAccelerationMode.OpenCL => FFmpegKnownHardwareAcceleration.OpenCL,
            HardwareAccelerationMode.Vulkan => FFmpegKnownHardwareAcceleration.Vulkan,
            HardwareAccelerationMode.Rkmpp => FFmpegKnownHardwareAcceleration.Rkmpp,
            _ => Option<FFmpegKnownHardwareAcceleration>.None
        };

        foreach (FFmpegKnownHardwareAcceleration accelToCheck in maybeAccelToCheck)
        {
            return ffmpegHardwareAccelerations.Contains(accelToCheck.Name);
        }

        return false;
    }

    public bool HasDecoder(FFmpegKnownDecoder decoder) => ffmpegDecoders.Contains(decoder.Name);

    public bool HasEncoder(FFmpegKnownEncoder encoder) => ffmpegEncoders.Contains(encoder.Name);

    public bool HasFilter(FFmpegKnownFilter filter) => ffmpegFilters.Contains(filter.Name);

    public bool HasFilterOption(FFmpegKnownFilter filter, string optionName) =>
        ffmpegFilterOptions.TryGetValue(filter.Name, out IReadOnlySet<string>? options) && options.Contains(optionName);

    public bool HasOption(FFmpegKnownOption ffmpegOption) => ffmpegOptions.Contains(ffmpegOption.Name);

    public bool HasDemuxFormat(FFmpegKnownFormat format) => ffmpegDemuxFormats.Contains(format.Name);

    public Option<IDecoder> SoftwareDecoderForVideoFormat(string videoFormat) =>
        videoFormat switch
        {
            VideoFormat.Hevc => new DecoderHevc(),
            VideoFormat.H264 => new DecoderH264(),
            VideoFormat.Mpeg1Video => new DecoderMpeg1Video(),
            VideoFormat.Mpeg2Video => new DecoderMpeg2Video(),
            VideoFormat.Vc1 => new DecoderVc1(),
            VideoFormat.MsMpeg4V2 => new DecoderMsMpeg4V2(),
            VideoFormat.MsMpeg4V3 => new DecoderMsMpeg4V3(),
            VideoFormat.Mpeg4 => new DecoderMpeg4(),
            VideoFormat.Vp9 => new DecoderVp9(),
            VideoFormat.Av1 => new DecoderAv1(ffmpegDecoders),

            VideoFormat.Raw or VideoFormat.RawVideo => new DecoderRawVideo(),
            VideoFormat.Undetermined => new DecoderImplicit(),
            VideoFormat.Copy => new DecoderImplicit(),
            VideoFormat.GeneratedImage => new DecoderImplicit(),

            _ => Option<IDecoder>.None
        };
}
