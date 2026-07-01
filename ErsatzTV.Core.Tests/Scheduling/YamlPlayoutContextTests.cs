using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
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
}
