using System.ComponentModel.DataAnnotations;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Core.Api.SmartCollections;
using ErsatzTV.Extensions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers.Api;

[ApiController]
[EndpointGroupName("general")]
public class SmartCollectionController(IMediator mediator) : ControllerBase
{
    [HttpGet("/api/collections/smart", Name = "GetSmartCollections")]
    public async Task<List<SmartCollectionResponseModel>> GetAll() =>
        await mediator.Send(new GetAllSmartCollectionsForApi());

    [HttpPost("/api/collections/smart/new", Name = "CreateSmartCollection")]
    public Task<IActionResult> AddOne(
        [Required] [FromBody]
        CreateSmartCollection request) =>
        mediator.Send(request).MapT(r => new CreateSmartCollectionResult(r.Id)).ToActionResult();

    [HttpPut("/api/collections/smart/update", Name = "UpdateSmartCollection")]
    public Task<IActionResult> UpdateOne([Required] [FromBody] UpdateSmartCollection request) =>
        mediator.Send(request).ToActionResult();

    [HttpDelete("/api/collections/smart/delete/{id:int}", Name = "DeleteSmartCollection")]
    public Task<IActionResult> DeleteSmartCollection(int id) =>
        mediator.Send(new DeleteSmartCollection(id)).ToActionResult();
}
