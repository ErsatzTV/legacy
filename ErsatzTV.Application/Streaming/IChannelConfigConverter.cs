using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;

namespace ErsatzTV.Application.Streaming;

public interface IChannelConfigConverter
{
    Task<Core.Next.Config.ChannelConfig> ToNext(
        ChannelViewModel channel,
        FFmpegProfileViewModel ffmpegProfile,
        CancellationToken cancellationToken);
}
