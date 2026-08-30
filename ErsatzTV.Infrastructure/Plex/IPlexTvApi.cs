using ErsatzTV.Infrastructure.Plex.Models;
using Refit;

namespace ErsatzTV.Infrastructure.Plex;

[Headers("Accept: application/json")]
public interface IPlexTvApi
{
    [Post("/pins")]
    Task<PlexPinResponse> StartPinFlow(
        [Query] [AliasAs("X-Plex-Product")]
        string product,
        [Query] [AliasAs("X-Plex-Version")]
        string version,
        [Query] [AliasAs("X-Plex-Client-Identifier")]
        string clientIdentifier,
        [Query]
        bool strong = true);

    [Get("/pins/{id}")]
    Task<PlexTokenResponse> GetPinStatus(
        int id,
        [Query]
        string code,
        [Query] [AliasAs("X-Plex-Client-Identifier")]
        string clientIdentifier);

    [Get("/user")]
    Task<PlexUserResponse> GetUser(
        [Query] [AliasAs("X-Plex-Product")]
        string product,
        [Query] [AliasAs("X-Plex-Version")]
        string version,
        [Query] [AliasAs("X-Plex-Client-Identifier")]
        string clientIdentifier,
        [Query] [AliasAs("X-Plex-Token")]
        string token);

    [Get("/resources")]
    Task<List<PlexResource>> GetResources(
        [Query] [AliasAs("includeHttps")]
        int includeHttps,
        [Header("X-Plex-Client-Identifier")]
        string clientIdentifier,
        [Header("X-Plex-Token")]
        string token);
}
