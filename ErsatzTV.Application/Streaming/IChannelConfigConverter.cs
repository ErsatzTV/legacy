using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Streaming;

public interface IChannelConfigConverter
{
    Task<Core.Next.Config.ChannelConfig> ToNext(
        ChannelViewModel channel,
        FFmpegProfileViewModel ffmpegProfile,
        Option<FrameRate> targetFramerate,
        CancellationToken cancellationToken);
}
