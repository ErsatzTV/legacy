using System.Net;
using System.Reflection;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Plex;
using ErsatzTV.Infrastructure.Plex.Models;
using Microsoft.Extensions.Logging;
using Refit;

namespace ErsatzTV.Infrastructure.Plex;

public class PlexTvApiClient : IPlexTvApiClient
{
    private static readonly string InfoVersion = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private const string AppName = "ErsatzTV";
    private readonly ILogger<PlexTvApiClient> _logger;
    private readonly IPlexSecretStore _plexSecretStore;

    private readonly IPlexTvApi _plexTvApi;

    public PlexTvApiClient(IPlexTvApi plexTvApi, IPlexSecretStore plexSecretStore, ILogger<PlexTvApiClient> logger)
    {
        _plexTvApi = plexTvApi;
        _plexSecretStore = plexSecretStore;
        _logger = logger;
    }

    public async Task<Either<BaseError, List<PlexMediaSource>>> GetServers()
    {
        try
        {
            var result = new List<PlexMediaSource>();
            string clientIdentifier = await _plexSecretStore.GetClientIdentifier();
            foreach (PlexUserAuthToken token in await _plexSecretStore.GetUserAuthTokens())
            {
                List<PlexResource> httpResources = await _plexTvApi.GetResources(
                    0,
                    clientIdentifier,
                    token.AuthToken);

                List<PlexResource> httpsResources = await _plexTvApi.GetResources(
                    1,
                    clientIdentifier,
                    token.AuthToken);

                var allServers = httpResources.Filter(resource => !resource.HttpsRequired)
                    .Append(httpsResources.Filter(resource => resource.HttpsRequired))
                    .Filter(r => r.Provides.Split(",").Any(p => p == "server"))
                    .ToList();

                IEnumerable<PlexResource> ownedServers = allServers;

                if (!SystemEnvironment.AllowSharedPlexServers)
                {
                    ownedServers = ownedServers.Filter(r => r.Owned);
                }

                foreach (PlexResource resource in ownedServers)
                {
                    var serverAuthToken = new PlexServerAuthToken(
                        resource.ClientIdentifier,
                        resource.AccessToken);

                    await _plexSecretStore.UpsertServerAuthToken(serverAuthToken);
                    List<PlexResourceConnection> sortedConnections = resource.HttpsRequired
                        ? resource.Connections
                        : resource.Connections.OrderBy(c => c.Local ? 0 : 1).ToList();

                    var source = new PlexMediaSource
                    {
                        ServerName = resource.Name,
                        ProductVersion = resource.ProductVersion,
                        Platform = resource.Platform,
                        PlatformVersion = resource.PlatformVersion,
                        ClientIdentifier = resource.ClientIdentifier,
                        Connections = sortedConnections
                            .Map(c => new PlexConnection { Uri = c.Uri }).ToList()
                    };

                    result.Add(source);
                }
            }

            return result;
        }
        catch (ApiException apiException)
        {
            if (apiException.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _plexSecretStore.DeleteAll();
            }

            return BaseError.New(apiException.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plex servers");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, PlexAuthPin>> StartPinFlow(bool forceNewCredentials)
    {
        try
        {
            // plex issues auth tokens per device, so signing in again with the stored client
            // identifier returns the token this device already has; a new identifier is what
            // makes plex mint a new one. It is not saved until the sign-in completes, so an
            // abandoned sign-in leaves the existing credentials alone.
            string clientIdentifier = forceNewCredentials
                ? _plexSecretStore.GenerateClientIdentifier()
                : await _plexSecretStore.GetClientIdentifier();

            PlexPinResponse pinResponse = await _plexTvApi.StartPinFlow(AppName, InfoVersion, clientIdentifier);
            return new PlexAuthPin(pinResponse.Id, pinResponse.Code, clientIdentifier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting plex pin flow");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<bool> TryCompletePinFlow(PlexAuthPin authPin)
    {
        try
        {
            PlexTokenResponse response = await _plexTvApi.GetPinStatus(
                authPin.Id,
                authPin.Code,
                authPin.ClientIdentifier);

            if (!string.IsNullOrWhiteSpace(response.AuthToken))
            {
                PlexUserResponse user = await _plexTvApi.GetUser(
                    AppName,
                    InfoVersion,
                    authPin.ClientIdentifier,
                    response.AuthToken);

                await _plexSecretStore.UpsertClientIdentifier(authPin.ClientIdentifier);

                var token = new PlexUserAuthToken(user.Email, user.AuthToken);
                await _plexSecretStore.UpsertUserAuthToken(token);

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing plex pin flow");
        }

        return false;
    }
}
