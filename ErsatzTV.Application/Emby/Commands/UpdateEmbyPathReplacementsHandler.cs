using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Emby;

public class UpdateEmbyPathReplacementsHandler : IRequestHandler<UpdateEmbyPathReplacements,
    Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public UpdateEmbyPathReplacementsHandler(
        IMediaSourceRepository mediaSourceRepository,
        IDbContextFactory<TvContext> dbContextFactory,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _dbContextFactory = dbContextFactory;
        _workerChannel = workerChannel;
    }

    public Task<Either<BaseError, Unit>> Handle(
        UpdateEmbyPathReplacements request,
        CancellationToken cancellationToken) =>
        Validate(request, cancellationToken)
            .MapT(pms => MergePathReplacements(request, pms, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Unit> MergePathReplacements(
        UpdateEmbyPathReplacements request,
        EmbyMediaSource embyMediaSource,
        CancellationToken cancellationToken)
    {
        embyMediaSource.PathReplacements ??= new List<EmbyPathReplacement>();

        var incoming = request.PathReplacements.Map(Project).ToList();

        var toAdd = incoming.Filter(r => r.Id < 1).ToList();
        var toRemove = embyMediaSource.PathReplacements.Filter(r => incoming.All(pr => pr.Id != r.Id)).ToList();
        var toUpdate = incoming.Except(toAdd).ToList();

        await _mediaSourceRepository.UpdatePathReplacements(embyMediaSource.Id, toAdd, toUpdate, toRemove);

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<string> channelNumbers = await NextPlayoutDependents.ForMediaSource(
            dbContext,
            embyMediaSource.Id,
            cancellationToken);
        await _workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return Unit.Default;
    }

    private static EmbyPathReplacement Project(EmbyPathReplacementItem vm) =>
        new() { Id = vm.Id, EmbyPath = vm.EmbyPath, LocalPath = vm.LocalPath };

    private Task<Validation<BaseError, EmbyMediaSource>> Validate(UpdateEmbyPathReplacements request, CancellationToken cancellationToken) =>
        EmbyMediaSourceMustExist(request, cancellationToken);

    private Task<Validation<BaseError, EmbyMediaSource>> EmbyMediaSourceMustExist(
        UpdateEmbyPathReplacements request, CancellationToken cancellationToken) =>
        _mediaSourceRepository.GetEmby(request.EmbyMediaSourceId, cancellationToken)
            .Map(v => v.ToValidation<BaseError>(
                $"Emby media source {request.EmbyMediaSourceId} does not exist."));
}
