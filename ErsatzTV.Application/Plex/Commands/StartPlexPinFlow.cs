using ErsatzTV.Core;

namespace ErsatzTV.Application.Plex;

public record StartPlexPinFlow(bool ForceNewCredentials) : IRequest<Either<BaseError, string>>;
