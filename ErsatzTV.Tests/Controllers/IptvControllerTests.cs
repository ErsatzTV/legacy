using ErsatzTV.Controllers;
using ErsatzTV.Core.FFmpeg;
using LanguageExt;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Controllers;

[TestFixture]
public class IptvControllerTests
{
    private const string ChannelNumber = "1";

    [Test]
    public async Task GetLivePlaylist_Should_Return_Playlist_When_Trim_Succeeds()
    {
        var trimResult = new TrimPlaylistResult(DateTimeOffset.Now, 0, 0, "#EXTM3U", 1);
        IptvController controller = MakeController(WorkerReturning(trimResult));

        IActionResult result = await controller.GetLivePlaylist(ChannelNumber, CancellationToken.None);

        var content = result.ShouldBeOfType<ContentResult>();
        content.Content.ShouldBe("#EXTM3U");
        content.ContentType.ShouldBe("application/vnd.apple.mpegurl");
    }

    [Test]
    public async Task GetLivePlaylist_Should_Ask_Client_To_Retry_When_Trim_Fails()
    {
        IptvController controller = MakeController(WorkerReturning(Option<TrimPlaylistResult>.None));

        IActionResult result = await controller.GetLivePlaylist(ChannelNumber, CancellationToken.None);

        // the session worker exists, so the failure is transient; a 404 would tell clients the
        // stream is gone and many would abandon it instead of polling again
        var statusCode = result.ShouldBeOfType<StatusCodeResult>();
        statusCode.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        controller.Response.Headers.RetryAfter.ToString().ShouldBe("1");
    }

    [Test]
    public async Task GetLivePlaylist_Should_Redirect_To_Start_Session_When_No_Worker()
    {
        var segmenterService = Substitute.For<IFFmpegSegmenterService>();
        segmenterService.TryGetWorker(Arg.Any<string>(), out Arg.Any<IHlsSessionWorker>()).Returns(false);

        IActionResult result = await MakeController(segmenterService)
            .GetLivePlaylist(ChannelNumber, CancellationToken.None);

        result.ShouldBeOfType<RedirectToActionResult>()
            .ActionName.ShouldBe(nameof(IptvController.GetHttpLiveStreamingVideo));
    }

    private static IFFmpegSegmenterService WorkerReturning(Option<TrimPlaylistResult> trimResult)
    {
        var worker = Substitute.For<IHlsSessionWorker>();
        worker.TrimPlaylist(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(trimResult);

        var segmenterService = Substitute.For<IFFmpegSegmenterService>();
        segmenterService.TryGetWorker(Arg.Any<string>(), out Arg.Any<IHlsSessionWorker>())
            .Returns(call =>
            {
                call[1] = worker;
                return true;
            });

        return segmenterService;
    }

    private static IptvController MakeController(IFFmpegSegmenterService segmenterService) =>
        new(
            Substitute.For<IMediator>(),
            Substitute.For<IGraphicsEngine>(),
            NullLogger<IptvController>.Instance,
            segmenterService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
}
