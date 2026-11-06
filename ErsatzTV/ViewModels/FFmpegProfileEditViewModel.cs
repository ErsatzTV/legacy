using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Resolutions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;

namespace ErsatzTV.ViewModels;

public class FFmpegProfileEditViewModel
{
    public FFmpegProfileEditViewModel()
    {
    }

    public FFmpegProfileEditViewModel(FFmpegProfileViewModel viewModel)
    {
        AudioBitrate = viewModel.AudioBitrate;
        AudioBufferSize = viewModel.AudioBufferSize;
        AudioChannels = viewModel.AudioChannels;
        AudioFormat = viewModel.AudioFormat;
        AudioSampleRate = viewModel.AudioSampleRate;
        NormalizeLoudnessMode = viewModel.NormalizeLoudnessMode;
        TargetLoudness = viewModel.TargetLoudness;
        Id = viewModel.Id;
        Name = viewModel.Name;
        NormalizeFramerate = viewModel.NormalizeFramerate;
        NormalizeColors = viewModel.NormalizeColors;
        DeinterlaceVideo = viewModel.DeinterlaceVideo;
        Resolution = viewModel.Resolution;
        ScalingBehavior = viewModel.ScalingBehavior;
        PadMode = viewModel.PadMode;
        ThreadCount = viewModel.ThreadCount;
        NormalizeAudio = viewModel.NormalizeAudio;
        NormalizeVideo = viewModel.NormalizeVideo;
        HardwareAcceleration = viewModel.HardwareAcceleration;
        VaapiDisplay = viewModel.VaapiDisplay;
        VaapiDriver = viewModel.VaapiDriver;
        VaapiDevice = viewModel.VaapiDevice;
        QsvExtraHardwareFrames = viewModel.QsvExtraHardwareFrames;
        VideoBitrate = viewModel.VideoBitrate;
        VideoBufferSize = viewModel.VideoBufferSize;
        VideoFormat = viewModel.VideoFormat;
        VideoProfile = viewModel.VideoProfile;
        VideoPreset = viewModel.VideoPreset;
        AllowBFrames = viewModel.AllowBFrames;
        BitDepth = viewModel.BitDepth;
        TonemapAlgorithm = viewModel.TonemapAlgorithm;
    }

    public int AudioBitrate { get; set; }
    public int AudioBufferSize { get; set; }
    public int AudioChannels { get; set; }

    public FFmpegProfileAudioFormat AudioFormat
    {
        get
        {
            if (field == FFmpegProfileAudioFormat.Copy && NormalizeAudio)
            {
                return FFmpegProfileAudioFormat.Aac;
            }

            return field;
        }

        set;
    }

    public int AudioSampleRate { get; set; }

    public NormalizeLoudnessMode NormalizeLoudnessMode
    {
        get;

        set
        {
            if (field != value)
            {
                field = value;
                if (field is NormalizeLoudnessMode.LoudNorm)
                {
                    TargetLoudness = -16;
                }
                else
                {
                    TargetLoudness = null;
                }
            }
        }
    }

    public double? TargetLoudness { get; set; }
    public int Id { get; set; }
    public string Name { get; set; }
    public bool NormalizeFramerate { get; set; }
    public bool NormalizeColors { get; set; }
    public bool DeinterlaceVideo { get; set; }
    public ResolutionViewModel Resolution { get; set; }
    public ScalingBehavior ScalingBehavior { get; set; }

    public FilterMode PadMode
    {
        // only allow customization with VAAPI accel
        get => HardwareAcceleration switch
        {
            HardwareAccelerationKind.None => FilterMode.Software,
            HardwareAccelerationKind.Vaapi => field,
            _ => FilterMode.HardwareIfPossible
        };

        set;
    }

    public int ThreadCount { get; set; }
    public bool NormalizeAudio { get; set; }
    public bool NormalizeVideo { get; set; }
    public HardwareAccelerationKind HardwareAcceleration { get; set; }
    public string VaapiDisplay { get; set; }
    public VaapiDriver VaapiDriver { get; set; }
    public string VaapiDevice { get; set; }
    public int? QsvExtraHardwareFrames { get; set; }
    public int VideoBitrate { get; set; }
    public int VideoBufferSize { get; set; }

    public FFmpegProfileVideoFormat VideoFormat
    {
        get
        {
            if (field == FFmpegProfileVideoFormat.Copy && NormalizeVideo)
            {
                return FFmpegProfileVideoFormat.H264;
            }

            return field;
        }

        set;
    }

    public string VideoProfile
    {
        get =>
            (HardwareAcceleration, VideoFormat, BitDepth) switch
            {
                (HardwareAccelerationKind.Nvenc, FFmpegProfileVideoFormat.H264, FFmpegProfileBitDepth.TenBit) => FFmpeg
                    .Format.VideoProfile.High444p,
                (_, FFmpegProfileVideoFormat.H264, _) => field,
                _ => string.Empty
            };

        set;
    }

    // without the legacy engine the profile field is hidden, but validation still checks it against bit depth
    public void SyncHiddenVideoProfile()
    {
        if (VideoFormat is not FFmpegProfileVideoFormat.H264)
        {
            return;
        }

        // the getter already forces high444p for nvenc 10-bit
        if (BitDepth is FFmpegProfileBitDepth.TenBit && HardwareAcceleration is not HardwareAccelerationKind.Nvenc)
        {
            VideoProfile = FFmpeg.Format.VideoProfile.High10;
        }
        else if (VideoProfile == FFmpeg.Format.VideoProfile.High10)
        {
            VideoProfile = FFmpeg.Format.VideoProfile.High;
        }
    }

    public string VideoPreset { get; set; }
    public bool AllowBFrames { get; set; }
    public FFmpegProfileBitDepth BitDepth { get; set; }
    public FFmpegProfileTonemapAlgorithm TonemapAlgorithm { get; set; }

    public CreateFFmpegProfile ToCreate() =>
        new(
            Name,
            ThreadCount,
            NormalizeAudio,
            NormalizeVideo,
            HardwareAcceleration,
            VaapiDisplay,
            VaapiDriver,
            VaapiDevice,
            QsvExtraHardwareFrames,
            Resolution.Id,
            ScalingBehavior,
            PadMode,
            VideoFormat,
            VideoProfile,
            VideoPreset,
            AllowBFrames,
            BitDepth,
            VideoBitrate,
            VideoBufferSize,
            TonemapAlgorithm,
            AudioFormat,
            AudioBitrate,
            AudioBufferSize,
            NormalizeLoudnessMode,
            TargetLoudness,
            AudioChannels,
            AudioSampleRate,
            NormalizeFramerate,
            NormalizeColors,
            DeinterlaceVideo
        );

    public UpdateFFmpegProfile ToUpdate() =>
        new(
            Id,
            Name,
            ThreadCount,
            NormalizeAudio,
            NormalizeVideo,
            HardwareAcceleration,
            VaapiDisplay,
            VaapiDriver,
            VaapiDevice,
            QsvExtraHardwareFrames,
            Resolution.Id,
            ScalingBehavior,
            PadMode,
            VideoFormat,
            VideoProfile,
            VideoPreset,
            AllowBFrames,
            BitDepth,
            VideoBitrate,
            VideoBufferSize,
            TonemapAlgorithm,
            AudioFormat,
            AudioBitrate,
            AudioBufferSize,
            NormalizeLoudnessMode,
            TargetLoudness,
            AudioChannels,
            AudioSampleRate,
            NormalizeFramerate,
            NormalizeColors,
            DeinterlaceVideo
        );
}
