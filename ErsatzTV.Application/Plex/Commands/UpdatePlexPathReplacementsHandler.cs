using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Plex;

public class
    UpdatePlexPathReplacementsHandler : IRequestHandler<UpdatePlexPathReplacements, Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public UpdatePlexPathReplacementsHandler(
        IMediaSourceRepository mediaSourceRepository,
        IDbContextFactory<TvContext> dbContextFactory,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _dbContextFactory = dbContextFactory;
        _workerChannel = workerChannel;
    }

    public Task<Either<BaseError, Unit>> Handle(
        UpdatePlexPathReplacements request,
        CancellationToken cancellationToken) =>
        Validate(request, cancellationToken)
            .MapT(pms => MergePathReplacements(request, pms, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Unit> MergePathReplacements(
        UpdatePlexPathReplacements request,
        PlexMediaSource plexMediaSource,
        CancellationToken cancellationToken)
    {
        plexMediaSource.PathReplacements ??= [];

        var incoming = request.PathReplacements.Map(Project).ToList();

        var toAdd = incoming.Filter(r => r.Id < 1).ToList();
        var toRemove = plexMediaSource.PathReplacements.Filter(r => incoming.All(pr => pr.Id != r.Id)).ToList();
        var toUpdate = incoming.Except(toAdd).ToList();

        await _mediaSourceRepository.UpdatePathReplacements(plexMediaSource.Id, toAdd, toUpdate, toRemove);

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<string> channelNumbers = await NextPlayoutDependents.ForMediaSource(
            dbContext,
            plexMediaSource.Id,
            cancellationToken);
        await _workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return Unit.Default;
    }

    private static PlexPathReplacement Project(PlexPathReplacementItem vm) =>
        new() { Id = vm.Id, PlexPath = vm.PlexPath, LocalPath = vm.LocalPath };

    private Task<Validation<BaseError, PlexMediaSource>> Validate(
        UpdatePlexPathReplacements request,
        CancellationToken cancellationToken) =>
        PlexMediaSourceMustExist(request, cancellationToken);

    private Task<Validation<BaseError, PlexMediaSource>> PlexMediaSourceMustExist(
        UpdatePlexPathReplacements request,
        CancellationToken cancellationToken) =>
        _mediaSourceRepository.GetPlex(request.PlexMediaSourceId, cancellationToken)
            .Map(v => v.ToValidation<BaseError>($"Plex media source {request.PlexMediaSourceId} does not exist."));
}
