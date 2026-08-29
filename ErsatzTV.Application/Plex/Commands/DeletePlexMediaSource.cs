using ErsatzTV.Core;

namespace ErsatzTV.Application.Plex;

public record DeletePlexMediaSource(int PlexMediaSourceId) : IRequest<Either<BaseError, Unit>>;
