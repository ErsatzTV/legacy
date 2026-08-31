using ErsatzTV.Core.Api.ScriptedPlayout;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.Engine;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers.Api;

[ApiController]
[EndpointGroupName("scripted-schedule")]
[Route("api/scripted/playout/build/{buildId:guid}")]
public class ScriptedScheduleController(IScriptedPlayoutBuilderService scriptedPlayoutBuilderService) : ControllerBase
{
    [HttpGet("context", Name = "GetContext")]
    [Tags("Scripted Metadata")]
    [EndpointSummary("Get the current context")]
    public ActionResult<PlayoutContext> GetContext([FromRoute] Guid buildId)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        return GetContextInternal(engine);
    }

    [HttpPost("add_collection", Name = "AddCollection")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a collection")]
    public async Task<IActionResult> AddCollection(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentCollection request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.Order, ignoreCase: true, out PlaybackOrder playbackOrder))
        {
            return BadRequest("Invalid playback order.");
        }

        await engine.AddCollection(request.Key, request.Collection, playbackOrder, cancellationToken);
        return Ok();
    }

    [HttpPost("add_marathon", Name = "AddMarathon")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a marathon")]
    public async Task<IActionResult> AddMarathon([FromRoute] Guid buildId, [FromBody] ContentMarathon request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.ItemOrder, ignoreCase: true, out PlaybackOrder itemPlaybackOrder))
        {
            return BadRequest("Invalid item playback order.");
        }

        if (!MarathonHelper.ValidGroupByValues.Contains(request.GroupBy, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(
                $"Invalid group by; valid values are {string.Join(", ", MarathonHelper.ValidGroupByValues)}.");
        }

        await engine.AddMarathon(
            request.Key,
            request.Guids ?? [],
            request.Searches ?? [],
            request.GroupBy,
            request.ShuffleGroups,
            itemPlaybackOrder,
            request.PlayAllItems);
        return Ok();
    }

    [HttpPost("add_multi_collection", Name = "AddMultiCollection")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a multi-collection")]
    public async Task<IActionResult> AddMultiCollection(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentMultiCollection request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.Order, ignoreCase: true, out PlaybackOrder playbackOrder))
        {
            return BadRequest("Invalid playback order.");
        }

        await engine.AddMultiCollection(request.Key, request.MultiCollection, playbackOrder, cancellationToken);
        return Ok();
    }

    [HttpPost("add_playlist", Name = "AddPlaylist")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a playlist")]
    public async Task<IActionResult> AddPlaylist(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentPlaylist request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.AddPlaylist(request.Key, request.Playlist, request.PlaylistGroup, cancellationToken);
        return Ok();
    }

    [HttpPost("create_playlist", Name="CreatePlaylist")]
    [Tags("Scripted Content")]
    [EndpointSummary("Create a playlist")]
    public async Task<IActionResult> CreatePlaylist(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentCreatePlaylist request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.CreatePlaylist(
            request.Key,
            request.Items.ToDictionary(i => i.Content, i => i.Count),
            cancellationToken);
        return Ok();
    }

    [HttpPost("add_search", Name = "AddSearch")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a search query")]
    public async Task<IActionResult> AddSearch(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentSearch request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.Order, ignoreCase: true, out PlaybackOrder playbackOrder))
        {
            return BadRequest("Invalid playback order.");
        }

        await engine.AddSearch(request.Key, request.Query, playbackOrder, cancellationToken);
        return Ok();
    }

    [HttpPost("add_smart_collection", Name = "AddSmartCollection")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a smart collection")]
    public async Task<IActionResult> AddSmartCollection(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentSmartCollection request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.Order, ignoreCase: true, out PlaybackOrder playbackOrder))
        {
            return BadRequest("Invalid playback order.");
        }

        await engine.AddSmartCollection(request.Key, request.SmartCollection, playbackOrder, cancellationToken);
        return Ok();
    }

    [HttpPost("add_show", Name = "AddShow")]
    [Tags("Scripted Content")]
    [EndpointSummary("Add a show")]
    public async Task<IActionResult> AddShow(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ContentShow request,
        CancellationToken cancellationToken)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (!Enum.TryParse(request.Order, ignoreCase: true, out PlaybackOrder playbackOrder))
        {
            return BadRequest("Invalid playback order.");
        }

        await engine.AddShow(request.Key, request.Guids, playbackOrder);
        return Ok();
    }

    [HttpPost("add_all", Name = "AddAll")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add all content")]
    public ActionResult<PlayoutContext> AddAll([FromRoute] Guid buildId, [FromBody] ContentAll request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.AddAll(request.Content, maybeFillerKind, request.CustomTitle, request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpPost("add_count", Name = "AddCount")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add a specific number of content items")]
    public ActionResult<PlayoutContext> AddCount(
        [FromRoute]
        Guid buildId,
        [FromBody]
        PlayoutCount request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.AddCount(
            request.Content,
            request.Count,
            maybeFillerKind,
            request.CustomTitle,
            request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpPost("add_duration", Name = "AddDuration")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add content for a specific duration")]
    public ActionResult<PlayoutContext> AddDuration(
        [FromRoute]
        Guid buildId,
        [FromBody]
        PlayoutDuration request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.AddDuration(
            request.Content,
            request.Duration,
            request.Fallback,
            request.Trim,
            request.DiscardAttempts,
            request.StopBeforeEnd,
            request.OfflineTail,
            maybeFillerKind,
            request.CustomTitle,
            request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpPost("pad_to_next", Name = "PadToNext")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add content until a specific minutes interval")]
    public ActionResult<PlayoutContext> PadToNext(
        [FromRoute] Guid buildId,
        [FromBody] PlayoutPadToNext request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.PadToNext(
            request.Content,
            request.Minutes,
            request.Fallback,
            request.Trim,
            request.DiscardAttempts,
            request.StopBeforeEnd,
            request.OfflineTail,
            maybeFillerKind,
            request.CustomTitle,
            request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpPost("pad_until", Name = "PadUntil")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add content until a specified time of day")]
    public ActionResult<PlayoutContext> PadUntil(
        [FromRoute] Guid buildId,
        [FromBody] PlayoutPadUntil request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.PadUntil(
            request.Content,
            request.When,
            request.Tomorrow,
            request.Fallback,
            request.Trim,
            request.DiscardAttempts,
            request.StopBeforeEnd,
            request.OfflineTail,
            maybeFillerKind,
            request.CustomTitle,
            request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpPost("pad_until_exact", Name = "PadUntilExact")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Add content until an exact time")]
    public ActionResult<PlayoutContext> PadUntilExact(
        [FromRoute]
        Guid buildId,
        [FromBody]
        PlayoutPadUntilExact request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<FillerKind> maybeFillerKind = Option<FillerKind>.None;
        if (Enum.TryParse(request.FillerKind, ignoreCase: true, out FillerKind fk))
        {
            maybeFillerKind = fk;
        }

        engine.PadUntilExact(
            request.Content,
            request.When,
            request.Fallback,
            request.Trim,
            request.DiscardAttempts,
            request.StopBeforeEnd,
            request.OfflineTail,
            maybeFillerKind,
            request.CustomTitle,
            request.DisableWatermarks);
        return GetContextInternal(engine);
    }

    [HttpGet("peek_next/{content}", Name="PeekNext")]
    [Tags("Scripted Scheduling")]
    [EndpointSummary("Peek the next content item")]
    public ActionResult<PeekItemDuration> PeekNext(Guid buildId, string content)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        Option<MediaItem> maybeMediaItem = engine.PeekNext(content);
        foreach (var mediaItem in maybeMediaItem)
        {
            return new PeekItemDuration
            {
                Content = content,
                Milliseconds = (long)mediaItem.GetDurationForPlayout().TotalMilliseconds
            };
        }

        return NotFound("Content key does not exist, or collection is empty");
    }

    [HttpPost("start_epg_group", Name = "StartEpgGroup")]
    [Tags("Scripted Control")]
    [EndpointSummary("Start an EPG group")]
    public IActionResult StartEpgGroup([FromRoute] Guid buildId, [FromBody] ControlStartEpgGroup request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.LockGuideGroup(request.Advance, request.CustomTitle);
        return Ok();
    }

    [HttpPost("stop_epg_group", Name = "StopEpgGroup")]
    [Tags("Scripted Control")]
    [EndpointSummary("Finish an EPG group")]
    public IActionResult StopEpgGroup([FromRoute] Guid buildId)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.UnlockGuideGroup();
        return Ok();
    }

    [HttpPost("graphics_on", Name = "GraphicsOn")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn on graphics elements")]
    public async Task<IActionResult> GraphicsOn(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ControlGraphicsOn request,
        CancellationToken cancellationToken = default)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.GraphicsOn(request.Graphics, request.Variables, cancellationToken);
        return Ok();
    }

    [HttpPost("graphics_off", Name = "GraphicsOff")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn off graphics elements")]
    public async Task<IActionResult> GraphicsOff(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ControlGraphicsOff request,
        CancellationToken cancellationToken = default)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.GraphicsOff(request.Graphics, cancellationToken);
        return Ok();
    }

    [HttpPost("watermark_on", Name = "WatermarkOn")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn on watermarks")]
    public async Task<IActionResult> WatermarkOn([FromRoute] Guid buildId, [FromBody] ControlWatermarkOn request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.WatermarkOn(request.Watermark);
        return Ok();
    }

    [HttpPost("watermark_off", Name = "WatermarkOff")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn off watermarks")]
    public async Task<IActionResult> WatermarkOff([FromRoute] Guid buildId, [FromBody] ControlWatermarkOff request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        await engine.WatermarkOff(request.Watermark);
        return Ok();
    }

    [HttpPost("pre_roll_on", Name = "PreRollOn")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn on pre-roll playlist")]
    public IActionResult PreRollOn([FromRoute] Guid buildId, [FromBody] ControlPreRollOn request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.PreRollOn(request.Playlist);
        return Ok();
    }

    [HttpPost("pre_roll_off", Name = "PreRollOff")]
    [Tags("Scripted Control")]
    [EndpointSummary("Turn off pre-roll playlist")]
    public IActionResult PreRollOff([FromRoute] Guid buildId)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.PreRollOff();
        return Ok();
    }

    [HttpPost("skip_items", Name = "SkipItems")]
    [Tags("Scripted Control")]
    [EndpointSummary("Skip a specific number of items")]
    public IActionResult SkipItems([FromRoute] Guid buildId, [FromBody] ControlSkipItems request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.SkipItems(request.Content, request.Count);
        return Ok();
    }

    [HttpPost("skip_to_item", Name = "SkipToItem")]
    [Tags("Scripted Control")]
    [EndpointSummary("Skip to a specific episode")]
    public IActionResult SkipToItem([FromRoute] Guid buildId, [FromBody] ControlSkipToItem request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.SkipToItem(request.Content, request.Season, request.Episode);
        return Ok();
    }

    [HttpPost("wait_until_exact", Name = "WaitUntilExact")]
    [Tags("Scripted Control")]
    [EndpointSummary("Wait until an exact time")]
    public ActionResult<PlayoutContext> WaitUntilExact(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ControlWaitUntilExact request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        engine.WaitUntilExact(request.When, request.RewindOnReset);

        return GetContextInternal(engine);
    }

    [HttpPost("wait_until", Name = "WaitUntil")]
    [Tags("Scripted Control")]
    [EndpointSummary("Wait until the specified time of day")]
    public ActionResult<PlayoutContext> WaitUntilTime(
        [FromRoute]
        Guid buildId,
        [FromBody]
        ControlWaitUntil request)
    {
        ISchedulingEngine engine = scriptedPlayoutBuilderService.GetEngine(buildId);
        if (engine == null)
        {
            return NotFound($"Active build engine not found for build {buildId}.");
        }

        if (TimeOnly.TryParse(request.When, out TimeOnly waitUntil))
        {
            engine.WaitUntil(waitUntil, request.Tomorrow, request.RewindOnReset);
        }

        return GetContextInternal(engine);
    }

    private ActionResult<PlayoutContext> GetContextInternal(ISchedulingEngine engine)
    {
        try
        {
            var state = engine.GetState();
            var responseModel = new PlayoutContext
            {
                CurrentTime = state.CurrentTime,
                StartTime = state.Start,
                FinishTime = state.Finish,
                IsDone = state.IsDone
            };
            return Ok(responseModel);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
