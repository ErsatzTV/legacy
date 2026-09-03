using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;

namespace ErsatzTV.Application.Plex;

public class DeletePlexMediaSourceHandler(IMediaSourceRepository mediaSourceRepository, ISearchIndex searchIndex)
    : IRequestHandler<DeletePlexMediaSource, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeletePlexMediaSource request,
        CancellationToken cancellationToken)
    {
        Option<PlexMediaSource> maybeMediaSource =
            await mediaSourceRepository.GetPlex(request.PlexMediaSourceId, cancellationToken);

        foreach (PlexMediaSource mediaSource in maybeMediaSource)
        {
            List<int> ids = await mediaSourceRepository.DeletePlex(mediaSource);
            await searchIndex.RemoveItems(ids);
            searchIndex.Commit();

            return Unit.Default;
        }

        return BaseError.New("Plex media source does not exist.");
    }
}
