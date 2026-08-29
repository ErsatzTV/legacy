using ErsatzTV.Core.Plex;

namespace ErsatzTV.Core.Interfaces.Plex;

public interface IPlexSecretStore
{
    Task<string> GetClientIdentifier();
    string GenerateClientIdentifier();
    Task<Unit> UpsertClientIdentifier(string clientIdentifier);
    Task<List<PlexUserAuthToken>> GetUserAuthTokens();
    Task<Unit> UpsertUserAuthToken(PlexUserAuthToken userAuthToken);
    Task<Option<PlexServerAuthToken>> GetServerAuthToken(string clientIdentifier);
    Task<Unit> UpsertServerAuthToken(PlexServerAuthToken serverAuthToken);
    Task<Unit> DeleteAll();
}
