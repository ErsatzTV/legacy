using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Scheduling;

public interface IPlayoutItemConverter
{
    Task<Option<Core.Next.PlayoutItem>> ToNext(
        string channelNumber,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken);

    Task<Option<Core.Next.PlayoutItem>> ToNext(
        Option<Channel> maybeChannel,
        Option<ChannelWatermark> maybeGlobalWatermark,
        TimeSpan playoutOffset,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken);
}
