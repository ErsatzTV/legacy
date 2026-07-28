using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class WatermarkSelectorChannelLogoTests
{
    private const string CachedLogoPath = "/tmp/logo";

    private static WatermarkSelector Selector()
    {
        var mockFileSystem = new MockFileSystem();
        mockFileSystem.Initialize().WithFile(CachedLogoPath);

        var fakeImageCache = Substitute.For<IImageCache>();
        fakeImageCache.GetPathForImage(Arg.Any<string>(), Arg.Is(ArtworkKind.Logo), Arg.Any<Option<int>>())
            .Returns(_ => CachedLogoPath);

        return new WatermarkSelector(
            mockFileSystem,
            fakeImageCache,
            new DecoSelector(),
            NullLogger<WatermarkSelector>.Instance);
    }

    private static Channel ChannelWithLogo(params Artwork[] artwork) =>
        new(Guid.Empty)
        {
            Id = 0,
            Name = "Test Channel",
            StreamingMode = StreamingMode.TransportStream,
            Artwork = [..artwork],
            Watermark = new ChannelWatermark
            {
                Id = 1,
                Name = "Channel",
                ImageSource = ChannelWatermarkImageSource.ChannelLogo
            }
        };

    [Test]
    public void Should_Use_External_Url_Channel_Logo()
    {
        const string ExternalUrl = "https://example.com/images/logo.png";

        Channel channel = ChannelWithLogo(new Artwork { ArtworkKind = ArtworkKind.Logo, Path = ExternalUrl });

        Option<WatermarkOptions> result = Selector().GetWatermarkOptions(
            channel,
            Option<ChannelWatermark>.None,
            Option<ChannelWatermark>.None,
            true);

        result.IsSome.ShouldBeTrue();
        foreach (WatermarkOptions options in result)
        {
            options.ImagePath.ShouldBe(ExternalUrl);
        }
    }

    [Test]
    public void Should_Use_Generated_Channel_Logo_Url_When_No_Logo_Artwork()
    {
        Channel channel = ChannelWithLogo();

        Option<WatermarkOptions> result = Selector().GetWatermarkOptions(
            channel,
            Option<ChannelWatermark>.None,
            Option<ChannelWatermark>.None,
            true);

        result.IsSome.ShouldBeTrue();
        foreach (WatermarkOptions options in result)
        {
            options.ImagePath.ShouldContain("/iptv/logos/gen");
        }
    }

    [Test]
    public void Should_Use_Cached_Channel_Logo()
    {
        Channel channel = ChannelWithLogo(new Artwork { ArtworkKind = ArtworkKind.Logo, Path = "logo.png" });

        Option<WatermarkOptions> result = Selector().GetWatermarkOptions(
            channel,
            Option<ChannelWatermark>.None,
            Option<ChannelWatermark>.None,
            true);

        result.IsSome.ShouldBeTrue();
        foreach (WatermarkOptions options in result)
        {
            options.ImagePath.ShouldBe(CachedLogoPath);
        }
    }

    [Test]
    public void Should_Ignore_Missing_Cached_Channel_Logo()
    {
        var mockFileSystem = new MockFileSystem();

        var fakeImageCache = Substitute.For<IImageCache>();
        fakeImageCache.GetPathForImage(Arg.Any<string>(), Arg.Is(ArtworkKind.Logo), Arg.Any<Option<int>>())
            .Returns(_ => "/tmp/missing-logo");

        var selector = new WatermarkSelector(
            mockFileSystem,
            fakeImageCache,
            new DecoSelector(),
            NullLogger<WatermarkSelector>.Instance);

        Channel channel = ChannelWithLogo(new Artwork { ArtworkKind = ArtworkKind.Logo, Path = "logo.png" });

        Option<WatermarkOptions> result = selector.GetWatermarkOptions(
            channel,
            Option<ChannelWatermark>.None,
            Option<ChannelWatermark>.None,
            true);

        result.IsNone.ShouldBeTrue();
    }
}
