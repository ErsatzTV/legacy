using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

public static class YamlPlayoutContextTests
{
    [TestFixture]
    public class ScheduleSwitching
    {
        private static YamlPlayoutContext CreateContext()
        {
            var definition = new YamlPlayoutDefinition
            {
                Playout = [new YamlPlayoutInstruction()],
                Schedules =
                [
                    new YamlPlayoutScheduleItem
                    {
                        Name = "Christmas",
                        StartDate = "12-25",
                        EndDate = "12-25",
                        Playout = [new YamlPlayoutInstruction()]
                    }
                ]
            };

            return new YamlPlayoutContext(new Playout(), definition, 1);
        }

        [Test]
        public void Switching_Should_Not_Leak_Graphics_Elements_Across_Lists()
        {
            YamlPlayoutContext context = CreateContext();

            // default playout turns a graphics element on
            context.SetGraphicsElement(1, null);
            context.GetGraphicsElements().ShouldContainKey(1);

            // crossing into the schedule should start with a clean ambient state
            context.SwitchToSchedule("Christmas");
            context.GetGraphicsElements().ShouldNotContainKey(1);

            // the schedule can turn on the same element without throwing
            Should.NotThrow(() => context.SetGraphicsElement(1, null));
            context.GetGraphicsElements().ShouldContainKey(1);
        }

        [Test]
        public void Returning_To_Default_Should_Restore_Its_Graphics_Elements()
        {
            YamlPlayoutContext context = CreateContext();

            context.SetGraphicsElement(1, "default-vars");
            context.SwitchToSchedule("Christmas");
            context.SetGraphicsElement(1, "christmas-vars");

            // returning to the default playout restores its ambient state
            context.SwitchToSchedule(null);
            context.GetGraphicsElements().ShouldContainKey(1);
            context.GetGraphicsElements()[1].ShouldBe("default-vars");
        }

        [Test]
        public void SetGraphicsElement_Should_Be_Idempotent()
        {
            YamlPlayoutContext context = CreateContext();

            context.SetGraphicsElement(1, "a");
            Should.NotThrow(() => context.SetGraphicsElement(1, "b"));
            context.GetGraphicsElements()[1].ShouldBe("b");
        }
    }

    [TestFixture]
    public class SequenceOrderPersistence
    {
        [Test]
        public void Restoring_Should_Keep_Shuffled_Orders_For_All_Playout_Lists()
        {
            YamlPlayoutDefinition savedDefinition = CreateDefinition([2, 0, 3, 1], [1, 3, 0, 2], true);
            var savedContext = new YamlPlayoutContext(new Playout(), savedDefinition, 1)
            {
                InstructionIndex = 3
            };
            savedContext.RestoreSequenceOrders();

            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = savedContext.Serialize()
            };

            YamlPlayoutDefinition restoredDefinition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false);
            var restoredContext = new YamlPlayoutContext(new Playout(), restoredDefinition, 1);
            restoredContext.Reset(anchor, DateTimeOffset.Now);
            restoredContext.RestoreSequenceOrders();

            restoredDefinition.Playout.Select(i => i.Content).ShouldBe(["show-2", "show-0", "show-3", "show-1"]);
            restoredDefinition.Schedules[0].Playout.Select(i => i.Content)
                .ShouldBe(["show-1", "show-3", "show-0", "show-2"]);
            restoredContext.InstructionIndex.ShouldBe(3);
        }

        [Test]
        public void Restoring_Should_Reset_The_List_When_The_Sequence_Changed()
        {
            YamlPlayoutDefinition savedDefinition = CreateDefinition([2, 0, 3, 1], [0, 1, 2, 3], true, "old");
            var savedContext = new YamlPlayoutContext(new Playout(), savedDefinition, 1)
            {
                InstructionIndex = 3
            };
            savedContext.RestoreSequenceOrders();
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = savedContext.Serialize()
            };

            YamlPlayoutDefinition restoredDefinition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false, "new");
            var restoredContext = new YamlPlayoutContext(new Playout(), restoredDefinition, 1);
            restoredContext.Reset(anchor, DateTimeOffset.Now);
            restoredContext.RestoreSequenceOrders();

            restoredDefinition.Playout.Select(i => i.Content).ShouldBe(["show-0", "show-1", "show-2", "show-3"]);
            restoredContext.InstructionIndex.ShouldBe(0);
        }

        [Test]
        public void Restoring_Should_Reset_The_List_When_Its_Layout_Changed()
        {
            YamlPlayoutDefinition savedDefinition = CreateDefinition([2, 0, 3, 1], [0, 1, 2, 3], true);
            var savedContext = new YamlPlayoutContext(new Playout(), savedDefinition, 1)
            {
                InstructionIndex = 3
            };
            savedContext.RestoreSequenceOrders();
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = savedContext.Serialize()
            };

            YamlPlayoutDefinition restoredDefinition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false);
            restoredDefinition.Playout.Insert(0, new YamlPlayoutInstruction { Content = "new-item" });
            var restoredContext = new YamlPlayoutContext(new Playout(), restoredDefinition, 1);
            restoredContext.Reset(anchor, DateTimeOffset.Now);
            restoredContext.RestoreSequenceOrders();

            restoredContext.InstructionIndex.ShouldBe(0);
        }

        [Test]
        public void Restoring_Should_Reset_The_List_For_Partial_Sequence_Orders()
        {
            List<YamlPlayoutInstruction> firstGroup = CreateInstructions([1, 0], true, "fingerprint");
            List<YamlPlayoutInstruction> secondGroup = CreateInstructions([0, 1], true, "fingerprint");
            var savedDefinition = new YamlPlayoutDefinition
            {
                Playout = firstGroup.Concat(secondGroup).ToList()
            };
            var savedContext = new YamlPlayoutContext(new Playout(), savedDefinition, 1)
            {
                InstructionIndex = 2
            };
            savedContext.RestoreSequenceOrders();
            YamlPlayoutContext.State savedState =
                JsonConvert.DeserializeObject<YamlPlayoutContext.State>(savedContext.Serialize());
            var partialOrders = new Dictionary<string, List<YamlPlayoutContext.SequenceOrder>>
            {
                [string.Empty] = [savedState.SequenceOrders[string.Empty][0]]
            };
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = JsonConvert.SerializeObject(savedState with { SequenceOrders = partialOrders })
            };

            var restoredDefinition = new YamlPlayoutDefinition
            {
                Playout = CreateInstructions([0, 1], false, "fingerprint")
                    .Concat(CreateInstructions([0, 1], false, "fingerprint"))
                    .ToList()
            };
            var restoredContext = new YamlPlayoutContext(new Playout(), restoredDefinition, 1);
            restoredContext.Reset(anchor, DateTimeOffset.Now);
            restoredContext.RestoreSequenceOrders();

            restoredContext.InstructionIndex.ShouldBe(0);
        }

        [Test]
        public void Restoring_Should_Reset_Sequence_Orders_Without_A_List_Fingerprint()
        {
            YamlPlayoutDefinition definition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false);
            var context = new YamlPlayoutContext(new Playout(), definition, 1);
            var incompleteState = new YamlPlayoutContext.State(
                2,
                1,
                false,
                [],
                null,
                SequenceOrders: new Dictionary<string, List<YamlPlayoutContext.SequenceOrder>>
                {
                    [string.Empty] = [new YamlPlayoutContext.SequenceOrder("shows", [2, 0, 3, 1])]
                });
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = JsonConvert.SerializeObject(incompleteState)
            };

            context.Reset(anchor, DateTimeOffset.Now);
            context.RestoreSequenceOrders();

            context.InstructionIndex.ShouldBe(0);
            definition.Playout.Select(i => i.Content).ShouldBe(["show-0", "show-1", "show-2", "show-3"]);
        }

        [Test]
        public void Restoring_Should_Reset_The_List_For_Malformed_Sequence_Orders()
        {
            YamlPlayoutDefinition definition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false);
            var context = new YamlPlayoutContext(new Playout(), definition, 1);
            var malformedState = new YamlPlayoutContext.State(
                2,
                1,
                false,
                [],
                null,
                SequenceOrders: new Dictionary<string, List<YamlPlayoutContext.SequenceOrder>>
                {
                    [string.Empty] = [new YamlPlayoutContext.SequenceOrder("shows", null)]
                });
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = JsonConvert.SerializeObject(malformedState)
            };

            context.Reset(anchor, DateTimeOffset.Now);
            Should.NotThrow(context.RestoreSequenceOrders);
            context.InstructionIndex.ShouldBe(0);
        }

        [Test]
        public void Restoring_Should_Accept_Anchors_Without_Sequence_Orders()
        {
            YamlPlayoutDefinition definition = CreateDefinition([0, 1, 2, 3], [0, 1, 2, 3], false);
            var context = new YamlPlayoutContext(new Playout(), definition, 1);
            var anchor = new PlayoutAnchor
            {
                NextStart = DateTime.UtcNow,
                Context = "{\"InstructionIndex\":2,\"GuideGroup\":1,\"GuideGroupLocked\":false," +
                          "\"ChannelWatermarkIds\":[],\"ScheduleIndices\":{\"\":2}}"
            };

            context.Reset(anchor, DateTimeOffset.Now);
            Should.NotThrow(context.RestoreSequenceOrders);

            definition.Playout.Select(i => i.Content).ShouldBe(["show-0", "show-1", "show-2", "show-3"]);
            context.InstructionIndex.ShouldBe(2);
        }

        private static YamlPlayoutDefinition CreateDefinition(
            int[] defaultOrder,
            int[] scheduleOrder,
            bool shuffled,
            string fingerprint = "fingerprint")
        {
            return new YamlPlayoutDefinition
            {
                Playout = CreateInstructions(defaultOrder, shuffled, fingerprint),
                Schedules =
                [
                    new YamlPlayoutScheduleItem
                    {
                        Name = "Christmas",
                        StartDate = "12-25",
                        EndDate = "12-25",
                        Playout = CreateInstructions(scheduleOrder, shuffled, fingerprint)
                    }
                ]
            };
        }

        private static List<YamlPlayoutInstruction> CreateInstructions(
            int[] order,
            bool shuffled,
            string fingerprint)
        {
            var sequenceGuid = Guid.NewGuid();
            return order
                .Select(index => new YamlPlayoutInstruction
                {
                    Content = $"show-{index}",
                    SequenceKey = "shows",
                    SequenceGuid = sequenceGuid,
                    SequenceIndex = index,
                    SequenceShuffled = shuffled,
                    SequenceFingerprint = fingerprint
                })
                .ToList();
        }
    }
}
