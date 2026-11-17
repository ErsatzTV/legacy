using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests;

[TestFixture]
public class UnsupportedJwtConfigTests
{
    [TestCase("/iptv/session/1/live.m3u8")]
    [TestCase("/iptv/session/1/live000001.ts")]
    [TestCase("/iptv/session/.troubleshooting/live.m3u8")]
    [TestCase("/iptv/channel/1.m3u8")]
    [TestCase("/iptv/channel/1.ts")]
    [TestCase("/iptv/channels.m3u")]
    [TestCase("/iptv/xmltv.xml")]
    [TestCase("/iptv/logos/gen")]
    [TestCase("/discover.json")]
    [TestCase("/device.xml")]
    [TestCase("/lineup.json")]
    [TestCase("/lineup_status.json")]
    public async Task Iptv_Paths_Should_Be_Blocked(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;

        await UnsupportedJwtConfig.BlockIptv(
            context,
            () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBe(UnsupportedJwtConfig.Message);
    }

    [TestCase("/")]
    [TestCase("/channels")]
    [TestCase("/api/channels")]
    [TestCase("/artwork/posters/abc")]
    [TestCase("/iptvfoo")]
    public async Task Other_Paths_Should_Pass_Through(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var nextCalled = false;

        await UnsupportedJwtConfig.BlockIptv(
            context,
            () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [TestCase("", false)]
    [TestCase("some-signing-key", true)]
    public void Should_Detect_Signing_Key(string key, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:IssuerSigningKey"] = key })
            .Build();

        UnsupportedJwtConfig.Init(configuration);

        UnsupportedJwtConfig.IsConfigured.ShouldBe(expected);
    }
}
