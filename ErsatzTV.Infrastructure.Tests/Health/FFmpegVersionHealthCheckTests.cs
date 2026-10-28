using ErsatzTV.Infrastructure.Health.Checks;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Health;

[TestFixture]
public class FFmpegVersionHealthCheckTests
{
    private static readonly FFmpegPin ReleasePin = FFmpegPin.Parse("""{ "tag": "8.1.2-6" }""");

    private static readonly FFmpegPin DevelopPin =
        FFmpegPin.Parse("""{ "tag": "8.1.2-1dee3b3b-develop", "last_release": "8.1.2-6" }""");

    [TestCase("n8.1.2-etv.5", FFmpegVersionStatus.Current)]
    [TestCase("8.1.2-etv.7", FFmpegVersionStatus.Current)]
    [TestCase("n8.1.2-etv.4", FFmpegVersionStatus.NotCurrent)]
    [TestCase("n8.1.2-etv.dev.1dee3b3b", FFmpegVersionStatus.OtherDevelop)]
    [TestCase("n8.1.1-etv.6", FFmpegVersionStatus.NotCurrent)]
    [TestCase("n8.1.2", FFmpegVersionStatus.NotCurrent)]
    [TestCase("7.1.1", FFmpegVersionStatus.TooOld)]
    public void Release_pin(string version, FFmpegVersionStatus expected) =>
        FFmpegVersionHealthCheck.Classify(ReleasePin, version).ShouldBe(expected);

    [TestCase("n8.1.2-etv.dev.1dee3b3b", FFmpegVersionStatus.Current)]
    [TestCase("n8.1.2-etv.dev.00000000", FFmpegVersionStatus.OtherDevelop)]
    [TestCase("n8.1.2-etv.7", FFmpegVersionStatus.Current)]
    [TestCase("n8.1.2-etv.6", FFmpegVersionStatus.NotCurrent)]
    [TestCase("n8.1.2-etv.5", FFmpegVersionStatus.NotCurrent)]
    [TestCase("7.1.1", FFmpegVersionStatus.TooOld)]
    public void Develop_pin(string version, FFmpegVersionStatus expected) =>
        FFmpegVersionHealthCheck.Classify(DevelopPin, version).ShouldBe(expected);

    [TestCase("""{ "tag": "8.1.2-1dee3b3b-develop" }""")]
    [TestCase("""{ "tag": "8.1.2-1dee3b3b-develop", "last_release": "8.1.1-9" }""")]
    [TestCase("""{ "tag": "latest" }""")]
    public void Rejects_bad_pin(string json) =>
        Should.Throw<InvalidOperationException>(() => FFmpegPin.Parse(json));

    [Test]
    public void Embeds_repository_pin() => FFmpegPin.Bundled.Value.Tag.ShouldNotBeNullOrWhiteSpace();
}
