using System.IO.Abstractions;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
using Subtitle = ErsatzTV.Core.Next.Config.Subtitle;

namespace ErsatzTV.Application.Streaming;

public class ChannelConfigConverter(IConfigElementRepository configElementRepository, IFileSystem fileSystem)
    : IChannelConfigConverter
{
    public async Task<ChannelConfig> ToNext(
        ChannelViewModel channel,
        FFmpegProfileViewModel ffmpegProfile,
        CancellationToken cancellationToken)
    {
        var ffmpeg = new Ffmpeg
        {
            // next only keeps errors, so always pass the folder
            ReportsFolder = FileSystemLayout.FFmpegReportsFolder
        };

        Option<string> ffmpegPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);

        foreach (string path in ffmpegPath)
        {
            ffmpeg.FfmpegPath = path;
        }

        Option<string> ffprobePath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);

        foreach (string path in ffprobePath)
        {
            ffmpeg.FfprobePath = path;
        }

        // Option<bool> maybeSaveReports = await configElementRepository.GetValue<bool>(
        //     ConfigElementKey.FFmpegSaveReports,
        //     cancellationToken);

        var audioNormalization = new Audio
        {
            Format = ffmpegProfile.AudioFormat switch
            {
                FFmpegProfileAudioFormat.Ac3 => AudioFormat.Ac3,
                _ => AudioFormat.Aac
            },
            BitrateKbps = ffmpegProfile.AudioBitrate,
            BufferKbps = ffmpegProfile.AudioBufferSize,
            Channels = ffmpegProfile.AudioChannels,
            SampleRateHz = ffmpegProfile.AudioSampleRate * 1000
        };

        if (ffmpegProfile.NormalizeLoudnessMode is NormalizeLoudnessMode.LoudNorm)
        {
            audioNormalization.NormalizeLoudness = true;
            audioNormalization.Loudness = new LoudnessClass
            {
                IntegratedTarget = ffmpegProfile.TargetLoudness
            };
        }

        string tonemapAlgorithm = ffmpegProfile.TonemapAlgorithm switch
        {
            FFmpegProfileTonemapAlgorithm.Clip => "clip",
            FFmpegProfileTonemapAlgorithm.Gamma => "gamma",
            FFmpegProfileTonemapAlgorithm.Reinhard => "reinhard",
            FFmpegProfileTonemapAlgorithm.Mobius => "mobius",
            FFmpegProfileTonemapAlgorithm.Hable => "hable",
            _ => "linear"
        };

        var videoNormalization = new Video
        {
            Format = ffmpegProfile.VideoFormat switch
            {
                FFmpegProfileVideoFormat.Hevc => VideoFormat.Hevc,
                _ => VideoFormat.H264
            },
            BitDepth = ffmpegProfile.BitDepth switch
            {
                FFmpegProfileBitDepth.TenBit => 10,
                _ => 8
            },
            Accel = ffmpegProfile.HardwareAcceleration switch
            {
                HardwareAccelerationKind.Amf => AccelEnum.Amf,
                HardwareAccelerationKind.Nvenc => AccelEnum.Cuda,
                HardwareAccelerationKind.Qsv => AccelEnum.Qsv,
                HardwareAccelerationKind.Rkmpp => AccelEnum.Rkmpp,
                HardwareAccelerationKind.Vaapi => AccelEnum.Vaapi,
                HardwareAccelerationKind.VideoToolbox => AccelEnum.Videotoolbox,
                _ => null
            },
            Height = ffmpegProfile.Resolution.Height,
            Width = ffmpegProfile.Resolution.Width,
            BitrateKbps = ffmpegProfile.VideoBitrate,
            BufferKbps = ffmpegProfile.VideoBufferSize,
            ScalingMode = ffmpegProfile.ScalingBehavior switch
            {
                ScalingBehavior.Stretch => ScalingMode.Stretch,
                ScalingBehavior.Crop => ScalingMode.Crop,
                _ => ScalingMode.ScaleAndPad
            },
            Deinterlace = ffmpegProfile.DeinterlaceVideo,
            Filters = new Filters
            {
                Tonemap = new TonemapClass
                {
                    Tonemap = tonemapAlgorithm
                },
                TonemapOpencl = new TonemapOpenclClass
                {
                    Tonemap = tonemapAlgorithm
                },
                Libplacebo = new LibplaceboClass
                {
                    Tonemapping = tonemapAlgorithm
                }
            },
            VaapiDevice = ffmpegProfile.VaapiDevice,
            VaapiDriver = ffmpegProfile.VaapiDriver switch
            {
                VaapiDriver.i965 => VaapiDriverEnum.I965,
                VaapiDriver.RadeonSI => VaapiDriverEnum.Radeonsi,
                _ => VaapiDriverEnum.Ihd
            }
        };

        var subtitleNormalization = new Subtitle
        {
            Mode = channel.NextEngineTextSubtitleMode switch
            {
                NextEngineTextSubtitleMode.Convert => Mode.Convert,
                _ => Mode.Burn
            },
            FontsFolder = FileSystemLayout.FontsCacheFolder
        };

        string playoutFolder = fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, channel.Number, "current");

        return new ChannelConfig
        {
            Playout = new Core.Next.Config.Playout
            {
                Folder = playoutFolder
            },
            Ffmpeg = ffmpeg,
            Normalization = new Normalization
            {
                Audio = audioNormalization,
                Video = videoNormalization,
                Subtitle = subtitleNormalization
            }
        };
    }
}
