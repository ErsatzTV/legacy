using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Plex;

namespace ErsatzTV.Core.Interfaces.Plex;

public interface IPlexTvApiClient
{
    Task<Either<BaseError, PlexAuthPin>> StartPinFlow(bool forceNewCredentials);
    Task<bool> TryCompletePinFlow(PlexAuthPin authPin);
    Task<Either<BaseError, List<PlexMediaSource>>> GetServers();
}
