using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Jellyfin;
using Microsoft.Extensions.Caching.Memory;

namespace ErsatzTV.Application.Jellyfin;

public class GetJellyfinConnectionParametersHandler : IRequestHandler<GetJellyfinConnectionParameters,
    Either<BaseError, JellyfinConnectionParametersViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IJellyfinSecretStore _jellyfinSecretStore;
    private readonly IMemoryCache _memoryCache;

    public GetJellyfinConnectionParametersHandler(
        IMemoryCache memoryCache,
        IMediaSourceRepository mediaSourceRepository,
        IJellyfinSecretStore jellyfinSecretStore)
    {
        _memoryCache = memoryCache;
        _mediaSourceRepository = mediaSourceRepository;
        _jellyfinSecretStore = jellyfinSecretStore;
    }

    public async Task<Either<BaseError, JellyfinConnectionParametersViewModel>> Handle(
        GetJellyfinConnectionParameters request,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(request, out JellyfinConnectionParametersViewModel parameters))
        {
            return parameters;
        }

        Either<BaseError, JellyfinConnectionParametersViewModel> maybeParameters =
            await Validate(cancellationToken)
                .MapT(cp => new JellyfinConnectionParametersViewModel(cp.ActiveConnection.Address, cp.ApiKey))
                .Map(v => v.ToEither<JellyfinConnectionParametersViewModel>());

        return maybeParameters.Match(
            p =>
            {
                _memoryCache.Set(request, p, TimeSpan.FromHours(1));
                return maybeParameters;
            },
            error => error);
    }

    private Task<Validation<BaseError, ConnectionParameters>> Validate(CancellationToken cancellationToken) =>
        JellyfinMediaSourceMustExist(cancellationToken)
            .BindT(MediaSourceMustHaveActiveConnection)
            .BindT(MediaSourceMustHaveApiKey);

    private Task<Validation<BaseError, JellyfinMediaSource>> JellyfinMediaSourceMustExist(
        CancellationToken cancellationToken) =>
        _mediaSourceRepository.GetAllJellyfin(cancellationToken).Map(list => list.HeadOrNone())
            .Map(v => v.ToValidation<BaseError>(
                "Jellyfin media source does not exist."));

    private Validation<BaseError, ConnectionParameters> MediaSourceMustHaveActiveConnection(
        JellyfinMediaSource jellyfinMediaSource)
    {
        Option<JellyfinConnection> maybeConnection = jellyfinMediaSource.Connections.FirstOrDefault();
        return maybeConnection.Map(connection => new ConnectionParameters(connection, null))
            .ToValidation<BaseError>("Jellyfin media source requires an active connection");
    }

    private async Task<Validation<BaseError, ConnectionParameters>> MediaSourceMustHaveApiKey(
        ConnectionParameters connectionParameters)
    {
        JellyfinSecrets secrets = await _jellyfinSecretStore.ReadSecrets();
        return Optional(secrets.Address == connectionParameters.ActiveConnection.Address)
            .Where(match => match)
            .Map(_ => connectionParameters with { ApiKey = secrets.ApiKey })
            .ToValidation<BaseError>("Jellyfin media source requires an api key");
    }

    private sealed record ConnectionParameters(JellyfinConnection ActiveConnection, string ApiKey);
}
