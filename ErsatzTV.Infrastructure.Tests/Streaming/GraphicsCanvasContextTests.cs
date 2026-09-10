using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Streaming.Graphics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Streaming;

[TestFixture]
public class GraphicsCanvasContextTests
{
    [TestCase(0, 44)]
    [TestCase(44, 44)]
    [TestCase(1760, 40)]
    public async Task Should_keep_scheduled_stop_independent_of_chunk(int offsetSeconds, int durationSeconds)
    {
        var loader = new GraphicsElementLoader(null!, null!, Substitute.For<ITemplateDataRepository>(),
            NullLogger<GraphicsElementLoader>.Instance);
        var factory = new GraphicsEngineContextFactory(loader);
        var start = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset finish = start.AddMinutes(30);
        var resolution = new Resolution { Width = 1920, Height = 1080 };
        var channel = new Channel(Guid.NewGuid())
        {
            Number = "1",
            FFmpegProfile = new FFmpegProfile { Resolution = resolution, ScalingBehavior = ScalingBehavior.Stretch }
        };

        var result = await factory.Create(
            channel, new Movie(), new MediaVersion(),
            [new WatermarkOptions(new ChannelWatermark(), "watermark.png", LanguageExt.Option<int>.None)],
            [], new FrameRate("24000/1001"), start, start, finish,
            TimeSpan.FromSeconds(offsetSeconds), TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.FromMinutes(30), CancellationToken.None);

        result.IsSome.ShouldBeTrue();
        foreach (var context in result)
        {
            context.TemplateVariables[MediaItemTemplateDataKey.Stop].ShouldBe(finish);
            context.TemplateVariables[MediaItemTemplateDataKey.StreamSeek].ShouldBe(TimeSpan.FromSeconds(offsetSeconds));
            context.Duration.ShouldBe(TimeSpan.FromSeconds(durationSeconds));
        }
    }
}
