using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Jellyfin;

public class UpdateJellyfinPathReplacementsHandler : IRequestHandler<UpdateJellyfinPathReplacements,
    Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public UpdateJellyfinPathReplacementsHandler(
        IMediaSourceRepository mediaSourceRepository,
        IDbContextFactory<TvContext> dbContextFactory,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _dbContextFactory = dbContextFactory;
        _workerChannel = workerChannel;
    }

    public Task<Either<BaseError, Unit>> Handle(
        UpdateJellyfinPathReplacements request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(pms => MergePathReplacements(request, pms, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Unit> MergePathReplacements(
        UpdateJellyfinPathReplacements request,
        JellyfinMediaSource jellyfinMediaSource,
        CancellationToken cancellationToken)
    {
        jellyfinMediaSource.PathReplacements ??= new List<JellyfinPathReplacement>();

        var incoming = request.PathReplacements.Map(Project).ToList();

        var toAdd = incoming.Filter(r => r.Id < 1).ToList();
        var toRemove = jellyfinMediaSource.PathReplacements.Filter(r => incoming.All(pr => pr.Id != r.Id)).ToList();
        var toUpdate = incoming.Except(toAdd).ToList();

        await _mediaSourceRepository.UpdatePathReplacements(jellyfinMediaSource.Id, toAdd, toUpdate, toRemove);

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<string> channelNumbers = await NextPlayoutDependents.ForMediaSource(
            dbContext,
            jellyfinMediaSource.Id,
            cancellationToken);
        await _workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return Unit.Default;
    }

    private static JellyfinPathReplacement Project(JellyfinPathReplacementItem vm) =>
        new() { Id = vm.Id, JellyfinPath = vm.JellyfinPath, LocalPath = vm.LocalPath };

    private Task<Validation<BaseError, JellyfinMediaSource>> Validate(UpdateJellyfinPathReplacements request) =>
        JellyfinMediaSourceMustExist(request);

    private Task<Validation<BaseError, JellyfinMediaSource>> JellyfinMediaSourceMustExist(
        UpdateJellyfinPathReplacements request) =>
        _mediaSourceRepository.GetJellyfin(request.JellyfinMediaSourceId)
            .Map(v => v.ToValidation<BaseError>(
                $"Jellyfin media source {request.JellyfinMediaSourceId} does not exist."));
}
