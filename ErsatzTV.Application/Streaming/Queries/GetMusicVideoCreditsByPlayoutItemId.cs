namespace ErsatzTV.Application.Streaming;

public record GetMusicVideoCreditsByPlayoutItemId(int PlayoutItemId, Option<long> SeekToMs) : IRequest<Option<string>>;
