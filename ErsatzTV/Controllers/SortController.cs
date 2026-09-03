using ErsatzTV.Application.MediaCollections;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class SortController : ControllerBase
{
    private readonly IMediator _mediator;

    public SortController(IMediator mediator) => _mediator = mediator;

    [HttpPost("media/collections/{collectionId:int}/items")]
    public Task SortCollectionItems(
        int collectionId,
        [FromForm]
        SortedMediaItemIds sortedMediaItemIds)
    {
        var ids = sortedMediaItemIds.Item.Map(int.Parse).ToList();

        var request = new UpdateCollectionCustomOrder(
            collectionId,
            ids.Map(i => new MediaItemCustomOrder(i, ids.IndexOf(i))).ToList());

        return _mediator.Send(request);
    }
}

public record SortedMediaItemIds(List<string> Item);
