namespace ErsatzTV.Application.Streaming;

public record GetSongVideoBackgroundByPlayoutItemId(int ChannelId, int PlayoutItemId) : IRequest<Option<string>>;
