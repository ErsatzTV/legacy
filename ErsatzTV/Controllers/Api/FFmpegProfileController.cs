using System.ComponentModel.DataAnnotations;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Core.Api.FFmpegProfiles;
using ErsatzTV.Extensions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers.Api;

[ApiController]
[EndpointGroupName("general")]
public class FFmpegProfileController(IMediator mediator) : ControllerBase
{
    [HttpGet("/api/ffmpeg/profiles", Name = "GetFFmpegProfiles")]
    public async Task<List<FFmpegFullProfileResponseModel>> GetAll(CancellationToken cancellationToken) =>
        await mediator.Send(new GetAllFFmpegProfilesForApi(), cancellationToken);

    [HttpPost("/api/ffmpeg/profiles/new", Name = "CreateFFmpegProfile")]
    public Task<IActionResult> AddOne(
        [Required] [FromBody]
        CreateFFmpegProfile request,
        CancellationToken cancellationToken) => mediator.Send(request, cancellationToken).ToActionResult();

    [HttpPut("/api/ffmpeg/profiles/update", Name = "UpdateFFmpegProfile")]
    public Task<IActionResult> UpdateOne(
        [Required] [FromBody]
        UpdateFFmpegProfile request,
        CancellationToken cancellationToken) => mediator.Send(request, cancellationToken).ToActionResult();

    [HttpDelete("/api/ffmpeg/delete/{id:int}", Name = "DeleteFFmpegProfile")]
    public Task<IActionResult> DeleteProfileAsync(int id, CancellationToken cancellationToken) =>
        mediator.Send(new DeleteFFmpegProfile(id), cancellationToken).ToActionResult();
}
