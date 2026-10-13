using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Channels;

public record ChannelStreamingSpecsViewModel(
    int Height,
    int Width,
    int Bitrate,
    FFmpegProfileVideoFormat VideoFormat,
    string VideoProfile,
    FFmpegProfileAudioFormat AudioFormat,
    NextEngineTextSubtitleMode NextEngineTextSubtitleMode);
