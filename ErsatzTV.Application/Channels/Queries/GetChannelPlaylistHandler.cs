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

            channel.StreamingMode = mode.ToLowerInvariant() switch
            {
                "segmenter" or "hls-direct" => StreamingMode.HttpLiveStreamingSegmenter,
                "ts-legacy" or "ts" => StreamingMode.TransportStreamHybrid,
                _ => channel.StreamingMode
            };

            result.Add(channel);
        }

        return result;
    }
}
