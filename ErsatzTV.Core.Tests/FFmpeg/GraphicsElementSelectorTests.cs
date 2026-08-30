using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Shouldly;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class GraphicsElementSelectorTests
{
    private static readonly GraphicsElementSelector GraphicsElementSelector;
    private static readonly DateTimeOffset Now = new(2025, 08, 17, 12, 0, 0, TimeSpan.FromHours(-5));

    private static readonly GraphicsElement GraphicsElementTemplateDeco;
    private static readonly GraphicsElement GraphicsElementDefaultDeco;
    private static readonly GraphicsElement GraphicsElementPlayoutItem;

    private static readonly Channel Channel;
    private static readonly Channel ChannelHlsDirect;

    private static readonly PlayoutItem PlayoutItemWithNoGraphics;
    private static readonly PlayoutItem PlayoutItemWithGraphics;
    private static readonly PlayoutItem PlayoutItemWithGraphicsAsFiller;

    private static readonly PlayoutItem TemplateDecoInherit;
    private static readonly PlayoutItem TemplateDecoDisable;
    private static readonly PlayoutItem TemplateDecoOverride;
    private static readonly PlayoutItem TemplateDecoMerge;
    private static readonly PlayoutItem TemplateDecoMergeFillerDisabled;

    private static readonly PlayoutItem DefaultDecoInherit;
    private static readonly PlayoutItem DefaultDecoDisable;
    private static readonly PlayoutItem DefaultDecoOverride;
    private static readonly PlayoutItem DefaultDecoMerge;

    private static readonly PlayoutItem TemplateDecoInheritDefaultDecoMerge;
    private static readonly PlayoutItem TemplateDecoMergeDefaultDecoInherit;
    private static readonly PlayoutItem TemplateDecoMergeDefaultDecoMerge;
    private static readonly PlayoutItem TemplateDecoOverrideDefaultDecoMerge;

    static GraphicsElementSelectorTests()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        var loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

        GraphicsElementSelector = new GraphicsElementSelector(
            new DecoSelector(),
            loggerFactory.CreateLogger<GraphicsElementSelector>());

        GraphicsElementTemplateDeco = new GraphicsElement { Id = 1, Path = "Template Deco GE" };
        GraphicsElementDefaultDeco = new GraphicsElement { Id = 2, Path = "Default Deco GE" };
        GraphicsElementPlayoutItem = new GraphicsElement { Id = 3, Path = "Playout Item GE" };

        Channel = new Channel(Guid.NewGuid());
        ChannelHlsDirect = new Channel(Guid.NewGuid()) { StreamingMode = StreamingMode.HttpLiveStreamingDirect };

        PlayoutItemWithNoGraphics = new PlayoutItem { PlayoutItemGraphicsElements = [] };
        PlayoutItemWithGraphics = new PlayoutItem
        {
            PlayoutItemGraphicsElements =
            [
                new PlayoutItemGraphicsElement { GraphicsElement = GraphicsElementPlayoutItem }
            ]
        };
        PlayoutItemWithGraphicsAsFiller = new PlayoutItem
        {
            PlayoutItemGraphicsElements =
            [
                new PlayoutItemGraphicsElement { GraphicsElement = GraphicsElementPlayoutItem }
            ],
            FillerKind = FillerKind.Tail
        };

        var decoWithInherit = new DecoTemplate
            { Items = [new DecoTemplateItem { Deco = new Deco { GraphicsElementsMode = DecoMode.Inherit } }] };
        var decoWithDisable = new DecoTemplate
            { Items = [new DecoTemplateItem { Deco = new Deco { GraphicsElementsMode = DecoMode.Disable } }] };
        var decoWithOverride = new DecoTemplate
        {
            Items =
            [
                new DecoTemplateItem
                {
                    Deco = new Deco
                    {
                        GraphicsElementsMode = DecoMode.Override,
                        DecoGraphicsElements =
                            [new DecoGraphicsElement { GraphicsElement = GraphicsElementTemplateDeco }]
                    }
                }
            ]
        };
        var decoWithMerge = new DecoTemplate
        {
            Items =
            [
                new DecoTemplateItem
                {
                    Deco = new Deco
                    {
                        GraphicsElementsMode = DecoMode.Merge,
                        DecoGraphicsElements =
                            [new DecoGraphicsElement { GraphicsElement = GraphicsElementTemplateDeco }],
                        UseGraphicsElementsDuringFiller = true
                    }
                }
            ]
        };

        var decoWithMergeFillerDisabled = new DecoTemplate
        {
            Items =
            [
                new DecoTemplateItem
                {
                    Deco = new Deco
                    {
                        GraphicsElementsMode = DecoMode.Merge,
                        DecoGraphicsElements =
                            [new DecoGraphicsElement { GraphicsElement = GraphicsElementTemplateDeco }],
                        UseGraphicsElementsDuringFiller = false
                    }
                }
            ]
        };

        var playoutWithTemplateDecoInherit = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithInherit
                }
            ]
        };

        var playoutWithTemplateDecoDisable = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithDisable
                }
            ]
        };

        var playoutWithTemplateDecoOverride = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithOverride
                }
            ]
        };

        var playoutWithTemplateDecoMerge = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithMerge
                }
            ]
        };

        var playoutWithTemplateDecoMergeFillerDisabled = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithMergeFillerDisabled
                }
            ]
        };

        TemplateDecoInherit = new PlayoutItem
            { Playout = playoutWithTemplateDecoInherit, PlayoutItemGraphicsElements = [] };
        TemplateDecoDisable = new PlayoutItem
            { Playout = playoutWithTemplateDecoDisable, PlayoutItemGraphicsElements = [] };
        TemplateDecoOverride = new PlayoutItem
            { Playout = playoutWithTemplateDecoOverride, PlayoutItemGraphicsElements = [] };
        TemplateDecoMerge = new PlayoutItem
            { Playout = playoutWithTemplateDecoMerge, PlayoutItemGraphicsElements = [] };
        TemplateDecoMergeFillerDisabled = new PlayoutItem
            { Playout = playoutWithTemplateDecoMergeFillerDisabled, FillerKind = FillerKind.Tail };

        var playoutWithDecoInherit = new Playout
            { Deco = new Deco { GraphicsElementsMode = DecoMode.Inherit }, Templates = [] };
        var playoutWithDecoDisable = new Playout
            { Deco = new Deco { GraphicsElementsMode = DecoMode.Disable }, Templates = [] };
        var playoutWithDecoOverride = new Playout
        {
            Deco = new Deco
            {
                GraphicsElementsMode = DecoMode.Override,
                DecoGraphicsElements = [new DecoGraphicsElement { GraphicsElement = GraphicsElementDefaultDeco }]
            },
            Templates = []
        };
        var playoutWithDecoMerge = new Playout
        {
            Deco = new Deco
            {
                GraphicsElementsMode = DecoMode.Merge,
                DecoGraphicsElements = [new DecoGraphicsElement { GraphicsElement = GraphicsElementDefaultDeco }]
            },
            Templates = []
        };

        DefaultDecoInherit = new PlayoutItem { Playout = playoutWithDecoInherit, PlayoutItemGraphicsElements = [] };
        DefaultDecoDisable = new PlayoutItem { Playout = playoutWithDecoDisable, PlayoutItemGraphicsElements = [] };
        DefaultDecoOverride = new PlayoutItem { Playout = playoutWithDecoOverride, PlayoutItemGraphicsElements = [] };
        DefaultDecoMerge = new PlayoutItem { Playout = playoutWithDecoMerge, PlayoutItemGraphicsElements = [] };

        var playoutWithTemplateInheritDefaultMerge = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithInherit
                }
            ],
            Deco = playoutWithDecoMerge.Deco
        };

        TemplateDecoInheritDefaultDecoMerge = new PlayoutItem
            { Playout = playoutWithTemplateInheritDefaultMerge, PlayoutItemGraphicsElements = [] };

        var playoutWithTemplateMergeDefaultInherit = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithMerge
                }
            ],
            Deco = playoutWithDecoInherit.Deco
        };

        TemplateDecoMergeDefaultDecoInherit = new PlayoutItem
            { Playout = playoutWithTemplateMergeDefaultInherit, PlayoutItemGraphicsElements = [] };

        var playoutWithTemplateMergeDefaultMerge = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithMerge
                }
            ],
            Deco = playoutWithDecoMerge.Deco
        };

        TemplateDecoMergeDefaultDecoMerge = new PlayoutItem
            { Playout = playoutWithTemplateMergeDefaultMerge, PlayoutItemGraphicsElements = [] };

        var playoutWithTemplateOverrideDefaultMerge = new Playout
        {
            Templates =
            [
                new PlayoutTemplate
                {
                    DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                    DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                    MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                    DecoTemplate = decoWithOverride
                }
            ],
            Deco = playoutWithDecoMerge.Deco
        };

        TemplateDecoOverrideDefaultDecoMerge = new PlayoutItem
            { Playout = playoutWithTemplateOverrideDefaultMerge, PlayoutItemGraphicsElements = [] };
    }

    private static IEnumerable<(Channel channel, PlayoutItem playoutItem, List<GraphicsElement> expected)>
        SelectGraphicsElementsTestCases()
    {
        // HLS direct streaming mode disables graphics
        yield return (ChannelHlsDirect, PlayoutItemWithGraphics, []);

        // no graphics configured
        yield return (Channel, PlayoutItemWithNoGraphics, []);

        // only playout item graphics
        yield return (Channel, PlayoutItemWithGraphics, [GraphicsElementPlayoutItem]);

        // template deco disable
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoDisable.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, []);

        // template deco override
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoOverride.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco]);

        // template deco merge
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoMerge.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco, GraphicsElementPlayoutItem]);

        // template deco inherit, default deco disable
        yield return (Channel, new PlayoutItem { Playout = new Playout { Templates = TemplateDecoInherit.Playout.Templates, Deco = DefaultDecoDisable.Playout.Deco }, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, []);

        // template deco inherit, default deco override
        yield return (Channel, new PlayoutItem { Playout = new Playout { Templates = TemplateDecoInherit.Playout.Templates, Deco = DefaultDecoOverride.Playout.Deco }, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementDefaultDeco]);

        // template deco inherit, default deco merge
        yield return (Channel, new PlayoutItem { Playout = new Playout { Templates = TemplateDecoInherit.Playout.Templates, Deco = DefaultDecoMerge.Playout.Deco }, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementDefaultDeco, GraphicsElementPlayoutItem]);

        // template deco merge, default deco merge
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoMergeDefaultDecoMerge.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco, GraphicsElementDefaultDeco, GraphicsElementPlayoutItem]);

        // template deco merge, default deco inherit
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoMergeDefaultDecoInherit.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco, GraphicsElementPlayoutItem]);

        // template deco override, default deco merge (template override should win)
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoOverrideDefaultDecoMerge.Playout, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco]);

        // filler item with template deco merge, filler disabled
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoMergeFillerDisabled.Playout, FillerKind = FillerKind.Tail, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, []);

        // filler item with template deco merge, filler enabled
        yield return (Channel, new PlayoutItem { Playout = TemplateDecoMerge.Playout, FillerKind = FillerKind.Tail, PlayoutItemGraphicsElements = PlayoutItemWithGraphics.PlayoutItemGraphicsElements }, [GraphicsElementTemplateDeco, GraphicsElementPlayoutItem]);
    }

    [TestCaseSource(nameof(SelectGraphicsElementsTestCases))]
    public void Should_Select_Appropriate_Graphics_Elements(
        (Channel channel, PlayoutItem playoutItem, List<GraphicsElement> expected) testCase)
    {
        List<PlayoutItemGraphicsElement> result = GraphicsElementSelector.SelectGraphicsElements(
            testCase.channel,
            testCase.playoutItem,
            Now,
            shouldLogMessages: true);

        result.Map(pige => pige.GraphicsElement).ShouldBe(testCase.expected);
    }
}
