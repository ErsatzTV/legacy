using System.Threading.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Plex;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Plex;

public class SynchronizePlexMediaSourcesHandler : PlexBaseConnectionHandler,
    IRequestHandler<SynchronizePlexMediaSources,
        Either<BaseError, List<PlexMediaSource>>>
{
    private const string LocalhostUri = "http://localhost:32400";

    private readonly ChannelWriter<IScannerBackgroundServiceRequest> _channel;
    private readonly IEntityLocker _entityLocker;
    private readonly ILogger<SynchronizePlexMediaSourcesHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IPlexSecretStore _plexSecretStore;
    private readonly IPlexServerApiClient _plexServerApiClient;
    private readonly IPlexTvApiClient _plexTvApiClient;

    public SynchronizePlexMediaSourcesHandler(
        IMediaSourceRepository mediaSourceRepository,
        IPlexTvApiClient plexTvApiClient,
        IPlexServerApiClient plexServerApiClient,
        IPlexSecretStore plexSecretStore,
        ChannelWriter<IScannerBackgroundServiceRequest> channel,
        IEntityLocker entityLocker,
        ILogger<SynchronizePlexMediaSourcesHandler> logger)
        : base(plexServerApiClient, mediaSourceRepository, logger)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _plexTvApiClient = plexTvApiClient;
        _plexServerApiClient = plexServerApiClient;
        _plexSecretStore = plexSecretStore;
        _channel = channel;
        _entityLocker = entityLocker;
        _logger = logger;
    }

    public async Task<Either<BaseError, List<PlexMediaSource>>> Handle(
        SynchronizePlexMediaSources request,
        CancellationToken cancellationToken)
    {
        // without credentials plex.tv is never asked, and the empty result would otherwise read as
        // "this account has no servers" and flag every media source as missing
        List<PlexUserAuthToken> userAuthTokens = await _plexSecretStore.GetUserAuthTokens();
        if (userAuthTokens.Count == 0)
        {
            _entityLocker.UnlockPlex();
            return new List<PlexMediaSource>();
        }

        Either<BaseError, List<PlexMediaSource>> maybeServers = await _plexTvApiClient.GetServers();

        foreach (BaseError error in maybeServers.LeftToSeq())
        {
            // SynchronizeAllServers releases the plex lock, and it does not run for this path
            _entityLocker.UnlockPlex();
            return error;
        }

        return await maybeServers.BindAsync(SynchronizeAllServers);
    }

    private async Task<Either<BaseError, List<PlexMediaSource>>> SynchronizeAllServers(
        List<PlexMediaSource> servers)
    {
        try
        {
            return await SynchronizeAllServersInner(servers);
        }
        finally
        {
            _entityLocker.UnlockPlex();
        }
    }

    private async Task<Either<BaseError, List<PlexMediaSource>>> SynchronizeAllServersInner(
        List<PlexMediaSource> servers)
    {
        List<PlexMediaSource> allExisting = await _mediaSourceRepository.GetAllPlex();
        foreach (PlexMediaSource server in servers)
        {
            await SynchronizeServer(allExisting, server);
        }

        // a server missing from plex.tv may only be unclaimed (signing out all devices does this),
        // and deleting it would take its libraries and all of its media with it; mark it instead and
        // let the user remove it explicitly once they know it is really gone
        DateTime now = DateTime.UtcNow;
        foreach (PlexMediaSource missing in allExisting.Filter(s =>
                     servers.All(pms => pms.ClientIdentifier != s.ClientIdentifier)))
        {
            if (missing.MissingSince is null)
            {
                _logger.LogWarning(
                    "Plex server {ServerName} is no longer listed at plex.tv; it will be skipped until it returns, or until it is removed",
                    missing.ServerName);

                await _mediaSourceRepository.SetPlexMissingSince(missing.Id, now);
            }
        }

        foreach (PlexMediaSource mediaSource in await _mediaSourceRepository.GetAllPlex())
        {
            if (mediaSource.MissingSince is null)
            {
                await _channel.WriteAsync(new SynchronizePlexLibraries(mediaSource.Id));
            }
        }

        return allExisting;
    }

    private async Task SynchronizeServer(List<PlexMediaSource> allExisting, PlexMediaSource server)
    {
        if (server.Connections.All(c => c.Uri != LocalhostUri))
        {
            var localhost = new PlexConnection
            {
                PlexMediaSource = server,
                PlexMediaSourceId = server.Id,
                Uri = LocalhostUri
            };

            server.Connections.Add(localhost);
        }

        Option<PlexMediaSource> maybeExisting =
            allExisting.Find(s => s.ClientIdentifier == server.ClientIdentifier);

        foreach (PlexMediaSource existing in maybeExisting)
        {
            existing.Platform = server.Platform;
            existing.PlatformVersion = server.PlatformVersion;
            existing.ProductVersion = server.ProductVersion;
            existing.ServerName = server.ServerName;
            var toAdd = server.Connections
                .Filter(connection => existing.Connections.All(c => c.Uri != connection.Uri)).ToList();
            var toRemove = existing.Connections
                .Filter(connection => server.Connections.All(c => c.Uri != connection.Uri)).ToList();
            await _mediaSourceRepository.Update(existing, toAdd, toRemove);

            // Update can fail silently, so clear this with its own write rather than relying on it
            if (existing.MissingSince is not null)
            {
                _logger.LogInformation("Plex server {ServerName} is listed at plex.tv again", server.ServerName);
                await _mediaSourceRepository.SetPlexMissingSince(existing.Id, null);
            }

            Option<PlexServerAuthToken> maybeToken = await _plexSecretStore.GetServerAuthToken(server.ClientIdentifier);
            if (maybeToken.IsNone)
            {
                _logger.LogError(
                    "Unable to activate Plex connection for server {Server} without auth token",
                    server.ServerName);
            }

            foreach (PlexServerAuthToken token in maybeToken)
            {
                await FindConnectionToActivate(existing, token);
            }
        }

        if (maybeExisting.IsNone)
        {
            await _mediaSourceRepository.Add(server);
            Option<PlexServerAuthToken> maybeToken = await _plexSecretStore.GetServerAuthToken(server.ClientIdentifier);
            if (maybeToken.IsNone)
            {
                _logger.LogError(
                    "Unable to activate Plex connection for server {Server} without auth token",
                    server.ServerName);
            }

            foreach (PlexServerAuthToken token in maybeToken)
            {
                await FindConnectionToActivate(server, token);
            }
        }
    }
}
