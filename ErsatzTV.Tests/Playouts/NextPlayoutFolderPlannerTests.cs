using System.Text.Json;
using ErsatzTV.Application.Playouts;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Playouts;

[TestFixture]
public class NextPlayoutFolderPlannerTests
{
    private const string Version = "https://ersatztv.org/playout/version/0.0.7";

    private DateTimeOffset _today;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _today = new DateTimeOffset(DateTime.Today);
        _now = _today.AddHours(10);
    }

    [Test]
    public void Trimming_finished_items_is_not_a_change()
    {
        NextPlayoutFileState existing = File(_today, 24, "a", "b", "c");
        NextPlayoutFileState desired = File(_today.AddHours(8), 16, "b", "c");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([existing], [desired], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.NoChange);
    }

    [Test]
    public void Finished_item_within_margin_is_still_compared()
    {
        DateTimeOffset start = _now.AddMinutes(-62);
        NextPlayoutFileState existing = File(start, 1, "old");
        NextPlayoutFileState desired = File(start, 1, "new");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([existing], [desired], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.FullSwap);
    }

    [Test]
    public void Change_to_todays_live_items_swaps_the_folder()
    {
        NextPlayoutFileState existing = File(_today, 24, "a", "b", "c");
        NextPlayoutFileState desired = File(_today, 24, "a", "b", "changed");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([existing], [desired], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.FullSwap);
    }

    [Test]
    public void Extended_future_day_is_replaced_in_place()
    {
        DateTimeOffset tomorrow = _today.AddDays(1);
        NextPlayoutFileState today = File(_today, 24, "a", "b");
        NextPlayoutFileState oldTomorrow = File(tomorrow, 10, "c");
        NextPlayoutFileState newTomorrow = File(tomorrow, 12, "c", "d");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([today, oldTomorrow], [today, newTomorrow], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.InPlace);
        plan.FilesToWrite.ShouldBe([newTomorrow.FileName]);
        plan.FilesToDelete.ShouldBe([oldTomorrow.FileName]);
    }

    [Test]
    public void New_future_day_is_added_in_place()
    {
        NextPlayoutFileState today = File(_today, 24, "a", "b");
        NextPlayoutFileState tomorrow = File(_today.AddDays(1), 24, "c");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([today], [today, tomorrow], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.InPlace);
        plan.FilesToWrite.ShouldBe([tomorrow.FileName]);
        plan.FilesToDelete.ShouldBeEmpty();
    }

    [Test]
    public void Fully_finished_day_is_deleted_in_place()
    {
        NextPlayoutFileState yesterday = File(_today.AddDays(-1), 24, "y");
        NextPlayoutFileState today = File(_today, 24, "a", "b");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([yesterday, today], [today], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.InPlace);
        plan.FilesToWrite.ShouldBeEmpty();
        plan.FilesToDelete.ShouldBe([yesterday.FileName]);
    }

    [Test]
    public void Change_to_a_day_starting_within_the_buffer_swaps_the_folder()
    {
        DateTimeOffset now = _today.AddHours(23).AddMinutes(30);
        DateTimeOffset tomorrow = _today.AddDays(1);
        NextPlayoutFileState today = File(_today, 24, "a");
        NextPlayoutFileState oldTomorrow = File(tomorrow, 24, "b");
        NextPlayoutFileState newTomorrow = File(tomorrow, 24, "changed");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([today, oldTomorrow], [today, newTomorrow], now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.FullSwap);
    }

    [Test]
    public void Version_change_swaps_the_folder()
    {
        NextPlayoutFileState existing = File(_today, 24, "a", "b");
        NextPlayoutFileState desired = FileWithVersion(
            _today,
            24,
            "https://ersatztv.org/playout/version/0.0.8",
            "a",
            "b");

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan([existing], [desired], _now);

        plan.Kind.ShouldBe(NextPlayoutFolderPlanKind.FullSwap);
    }

    [TestCase("not-a-window.json", """{"version":"v","items":[]}""")]
    [TestCase("1_2.json", "not json")]
    [TestCase("1_2.json", """{"items":[]}""")]
    public void Unreadable_file_has_no_state(string fileName, string json) =>
        NextPlayoutFileState.FromJson(fileName, json, _now).IsNone.ShouldBeTrue();

    private NextPlayoutFileState File(DateTimeOffset start, int hours, params string[] ids) =>
        FileWithVersion(start, hours, Version, ids);

    // even split; dropping leading ids models trimmed finished items
    private NextPlayoutFileState FileWithVersion(
        DateTimeOffset start,
        int hours,
        string version,
        params string[] ids)
    {
        TimeSpan each = TimeSpan.FromHours(hours) / ids.Length;
        var items = ids.Select((id, index) => new
        {
            id,
            start = start + each * index,
            finish = start + each * (index + 1)
        }).ToList();

        DateTimeOffset finish = items[^1].finish;
        string fileName = $"{start.ToUnixTimeMilliseconds()}_{finish.ToUnixTimeMilliseconds()}.json";
        string json = JsonSerializer.Serialize(new { version, items });

        return NextPlayoutFileState.FromJson(fileName, json, NextPlayoutFolderPlanner.Cutoff(_now))
            .IfNone(() => throw new InvalidOperationException("Unreadable test file"));
    }
}
