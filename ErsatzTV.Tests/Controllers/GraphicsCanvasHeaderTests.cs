using System.Globalization;
using ErsatzTV.Controllers;
using ErsatzTV.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Controllers;

[TestFixture]
public class GraphicsCanvasHeaderTests
{
    [TestCase("x-etv-frame-rate", "garbage")]
    [TestCase("x-etv-frame-rate", "25/0")]
    [TestCase("x-etv-frame-rate", "0/1")]
    [TestCase("x-etv-frame-rate", "-25/1")]
    [TestCase("x-etv-frame-rate", "25/1/1")]
    [TestCase("x-etv-frame-rate", "NaN")]
    [TestCase("x-etv-frame-rate", "Infinity")]
    [TestCase("x-etv-frame-rate", "0")]
    [TestCase("x-etv-frame-rate", "")]
    [TestCase("x-etv-offset-ms", "-1")]
    [TestCase("x-etv-offset-ms", "922337203685478")]
    [TestCase("x-etv-duration-ms", "922337203685478")]
    [TestCase("x-etv-duration-ms", "0")]
    [TestCase("x-etv-channel", "2")]
    public async Task Should_reject_invalid_headers_before_dispatch(string header, string value)
    {
        // These requests must fail before any service is called.
        var controller = new InternalController(null!, null!, null!, null!, null!,
            NullLogger<InternalController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["x-etv-offset-ms"] = "0";
        controller.Request.Headers["x-etv-duration-ms"] = "44000";
        controller.Request.Headers["x-etv-frame-rate"] = "24000/1001";
        controller.Request.Headers["x-etv-channel"] = "1";
        controller.Request.Headers[header] = value;

        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(5);
        string signature = InternalUrlSigner.Sign(expires, "graphics", "1", "123");
        IActionResult result = await controller.GetGraphicsCanvas(
            "1", 123,
            expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), signature);

        result.ShouldBeOfType<BadRequestResult>();
    }
}
