using System.Threading.Channels;
using ErsatzTV.Application;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Core;
using ErsatzTV.Extensions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers.Api;

[ApiController]
[EndpointGroupName("general")]
public class MaintenanceController(IMediator mediator, ChannelWriter<IBackgroundServiceRequest> workerChannel)
{
    [HttpGet("/api/maintenance/gc")]
    [Tags("Maintenance")]
    [EndpointSummary("Garbage collect")]
    public async Task<IActionResult> GarbageCollection([FromQuery] bool force = false)
    {
        await mediator.Send(new ReleaseMemory(force));
        return new OkResult();
    }

    [HttpPost("/api/maintenance/empty_trash")]
    [Tags("Maintenance")]
    [EndpointSummary("Empty trash")]
    public Task<IActionResult> EmptyTrash() => mediator.Send(new EmptyTrash()).ToActionResult();

    [HttpPost("/api/maintenance/clean_artwork")]
    [Tags("Maintenance")]
    [EndpointSummary("Clean artwork cache")]
    public async Task<IActionResult> CleanArtwork(CancellationToken cancellationToken, [FromQuery] int limit = 100_000)
    {
        await workerChannel.WriteAsync(new DeleteOrphanedArtwork(limit), cancellationToken);
        return new OkResult();
    }
}
