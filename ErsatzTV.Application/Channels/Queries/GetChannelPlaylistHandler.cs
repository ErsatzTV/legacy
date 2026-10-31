using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Iptv;

namespace ErsatzTV.Application.Channels;

public class GetChannelPlaylistHandler(IChannelRepository channelRepository)
    : IRequestHandler<GetChannelPlaylist, ChannelPlaylist>
{
    public Task<ChannelPlaylist> Handle(GetChannelPlaylist request, CancellationToken cancellationToken) =>
        channelRepository.GetAll(cancellationToken)
            .Map(channels => EnsureMode(channels, request.Mode))
            .Map(channels => new ChannelPlaylist(
                request.Scheme,
                request.Host,
                request.BaseUrl,
                channels,
                request.UserAgent,
                request.AccessToken));

    private static List<Channel> EnsureMode(IEnumerable<Channel> channels, string mode)
    {
        var result = new List<Channel>();
        foreach (Channel channel in channels)
        {
            if (!channel.IsEnabled)
            {
                continue;
            }

            switch (mode.ToLowerInvariant())
            {
                case "segmenter":
                    channel.StreamingMode = StreamingMode.HttpLiveStreamingSegmenter;
                    result.Add(channel);
                    break;
                case "hls-direct":
                    channel.StreamingMode = LegacyStreamingEngine.Enabled
                        ? StreamingMode.HttpLiveStreamingDirect
                        : StreamingMode.HttpLiveStreamingSegmenter;
                    result.Add(channel);
                    break;
                case "ts-legacy":
                    channel.StreamingMode = LegacyStreamingEngine.Enabled
                        ? StreamingMode.TransportStream
                        : StreamingMode.TransportStreamHybrid;
                    result.Add(channel);
                    break;
                case "ts":
                    channel.StreamingMode = StreamingMode.TransportStreamHybrid;
                    result.Add(channel);
                    break;
                default:
                    result.Add(channel);
                    break;
            }
        }

        return result;
    }
}
