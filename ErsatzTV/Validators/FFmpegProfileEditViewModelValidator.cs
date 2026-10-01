using ErsatzTV.Core.Domain;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.ViewModels;
using FluentValidation;
using FluentValidation.Results;

namespace ErsatzTV.Validators;

public class FFmpegProfileEditViewModelValidator : AbstractValidator<FFmpegProfileEditViewModel>
{
    private static readonly List<FFmpegProfileVideoFormat> QsvFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc,
        FFmpegProfileVideoFormat.Mpeg2Video
    ];

    private static readonly List<FFmpegProfileVideoFormat> NvencFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc
    ];

    private static readonly List<FFmpegProfileVideoFormat> VaapiFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc,
        FFmpegProfileVideoFormat.Mpeg2Video
    ];

    private static readonly List<FFmpegProfileVideoFormat> VideoToolboxFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc
    ];

    private static readonly List<FFmpegProfileVideoFormat> AmfFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc
    ];

    private static readonly List<FFmpegProfileVideoFormat> V4l2m2mFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc
    ];

    private static readonly List<FFmpegProfileVideoFormat> RkmppFormats =
    [
        FFmpegProfileVideoFormat.H264,
        FFmpegProfileVideoFormat.Hevc
    ];
    public FFmpegProfileEditViewModelValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.ThreadCount).GreaterThanOrEqualTo(0);

        RuleFor(x => x.VideoFormat).NotEqual(FFmpegProfileVideoFormat.None);
        RuleFor(x => x.VideoBitrate).GreaterThan(0);
        RuleFor(x => x.VideoBufferSize).GreaterThan(0);

        RuleFor(x => x.AudioFormat).NotEqual(FFmpegProfileAudioFormat.None);
        RuleFor(x => x.AudioBitrate).GreaterThan(0);
        RuleFor(x => x.AudioChannels).GreaterThan(0);

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Qsv,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => QsvFormats.Contains(c))
                    .WithMessage("QSV supports formats (h264, hevc, mpeg2video)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Nvenc,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => NvencFormats.Contains(c))
                    .WithMessage("NVENC supports formats (h264, hevc)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Vaapi,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => VaapiFormats.Contains(c))
                    .WithMessage("VAAPI supports formats (h264, hevc, mpeg2video)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.VideoToolbox,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => VideoToolboxFormats.Contains(c))
                    .WithMessage("VideoToolbox supports formats (h264, hevc)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Amf,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => AmfFormats.Contains(c))
                    .WithMessage("Amf supports formats (h264, hevc)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.V4l2m2m,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => V4l2m2mFormats.Contains(c))
                    .WithMessage("V4L2 M2M supports formats (h264, hevc)");
            });

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Rkmpp,
            () =>
            {
                RuleFor(x => x.VideoFormat).Must(c => RkmppFormats.Contains(c))
                    .WithMessage("Rkmpp supports formats (h264, hevc)");
            });

        When(
            x => x.VideoFormat == FFmpegProfileVideoFormat.Mpeg2Video,
            () => RuleFor(x => x.BitDepth)
                .Must(bd => bd is FFmpegProfileBitDepth.EightBit)
                .WithMessage("Mpeg2Video does not support 10-bit content"));

        When(
            x => x.HardwareAcceleration != HardwareAccelerationKind.Nvenc && x.VideoFormat == FFmpegProfileVideoFormat.H264 && x.BitDepth == FFmpegProfileBitDepth.TenBit,
            () => RuleFor(x => x.VideoProfile)
                .Must(vp => vp == VideoProfile.High10)
                .WithMessage("VideoProfile must be high10 with 10-bit h264"));

        When(
            x => x.HardwareAcceleration == HardwareAccelerationKind.Nvenc && x.VideoFormat == FFmpegProfileVideoFormat.H264 && x.BitDepth == FFmpegProfileBitDepth.TenBit,
            () => RuleFor(x => x.VideoProfile)
                .Must(vp => vp == VideoProfile.High444p)
                .WithMessage("VideoProfile must be high444p with NVIDIA 10-bit h264"));

        When(
            x => x.VideoFormat == FFmpegProfileVideoFormat.H264 && x.BitDepth == FFmpegProfileBitDepth.EightBit,
            () => RuleFor(x => x.VideoProfile)
                .Must(vp => vp != VideoProfile.High10)
                .WithMessage("VideoProfile cannot be high10 with 8-bit h264"));
    }

    public Func<object, string, Task<IEnumerable<string>>> ValidateValue => async (model, propertyName) =>
    {
        ValidationResult result = await ValidateAsync(
            ValidationContext<FFmpegProfileEditViewModel>.CreateWithOptions(
                (FFmpegProfileEditViewModel)model,
                x => x.IncludeProperties(propertyName)));
        if (result.IsValid)
        {
            return [];
        }

        return result.Errors.Select(e => e.ErrorMessage);
    };
}
