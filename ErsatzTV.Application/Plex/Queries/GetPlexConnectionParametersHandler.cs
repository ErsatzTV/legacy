using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Plex;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Plex;

public class GetPlexConnectionParametersHandler : PlexBaseConnectionHandler,
    IRequestHandler<GetPlexConnectionParameters,
        Either<BaseError, PlexConnectionParametersViewModel>>
{
    private readonly ILogger<GetPlexConnectionParametersHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IPlexSecretStore _plexSecretStore;
    private readonly IPlexServerApiClient _plexServerApiClient;

    public GetPlexConnectionParametersHandler(
        IMemoryCache memoryCache,
        IPlexServerApiClient plexServerApiClient,
        IMediaSourceRepository mediaSourceRepository,
        IPlexSecretStore plexSecretStore,
        ILogger<GetPlexConnectionParametersHandler> logger)
        : base(plexServerApiClient, mediaSourceRepository, logger)
    {
        _memoryCache = memoryCache;
        _plexServerApiClient = plexServerApiClient;
        _mediaSourceRepository = mediaSourceRepository;
        _plexSecretStore = plexSecretStore;
        _logger = logger;
    }

    public async Task<Either<BaseError, PlexConnectionParametersViewModel>> Handle(
        GetPlexConnectionParameters request,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(request, out PlexConnectionParametersViewModel parameters))
        {
            return parameters;
        }

        Option<PlexMediaSource> maybeMediaSource =
            await _mediaSourceRepository.GetPlex(request.PlexMediaSourceId, cancellationToken);
        foreach (PlexMediaSource mediaSource in maybeMediaSource)
        {
            Option<PlexServerAuthToken> maybeToken =
                await _plexSecretStore.GetServerAuthToken(mediaSource.ClientIdentifier);
            foreach (PlexServerAuthToken token in maybeToken)
            {
                // try to keep the same connection
                Option<PlexConnection> maybeActiveConnection =
                    mediaSource.Connections.Filter(c => c.IsActive).HeadOrNone();
                foreach (PlexConnection activeConnection in maybeActiveConnection)
                {
                    if (await _plexServerApiClient.Ping(activeConnection, token, cancellationToken))
                    {
                        _logger.LogDebug("Plex connection is still active at {Uri}", activeConnection.Uri);
                        var p = new PlexConnectionParametersViewModel(activeConnection.Uri, token.AuthToken);
                        _memoryCache.Set(request, p, TimeSpan.FromSeconds(30));
                        return p;
                    }
                }

                _logger.LogInformation("Plex connection is no longer active, searching for a new connection");

                // check all connections for a working one
                Option<PlexConnection> maybeConnection = await FindConnectionToActivate(mediaSource, token);
                foreach (PlexConnection connection in maybeConnection)
                {
                    var p = new PlexConnectionParametersViewModel(connection.Uri, token.AuthToken);
                    _memoryCache.Set(request, p, TimeSpan.FromMinutes(30));
                    return p;
                }

                return BaseError.New($"Plex media source {request.PlexMediaSourceId} requires an active connection");
            }

            return BaseError.New($"Plex media source {request.PlexMediaSourceId} requires a token");
        }

        return BaseError.New($"Plex media source {request.PlexMediaSourceId} does not exist.");
    }
}
