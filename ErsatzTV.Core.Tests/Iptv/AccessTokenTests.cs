using ErsatzTV.Core.Iptv;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Iptv;

[TestFixture]
public class AccessTokenTests
{
    [TestCase("eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjF9.abc-DEF_123")]
    public void Valid_Token_Should_Pass(string token) => AccessToken.Sanitize(token).ShouldBe(token);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("abc def")]
    [TestCase("abc&mode=ts")]
    [TestCase("abc\"def")]
    [TestCase("abc%20def")]
    [TestCase("abc\n")]
    [TestCase("abc<def>")]
    [TestCase("abc!def")]
    [TestCase("abc^def")]
    [TestCase("abc/def")]
    public void Invalid_Token_Should_Be_Dropped(string token) => AccessToken.Sanitize(token).ShouldBeNull();
}
