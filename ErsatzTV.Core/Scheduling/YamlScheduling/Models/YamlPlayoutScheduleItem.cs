using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutScheduleItem
{
    public string Name { get; set; }

    [YamlMember(Alias = "start_date", ApplyNamingConventions = false)]
    public string StartDate { get; set; }

    [YamlMember(Alias = "end_date", ApplyNamingConventions = false)]
    public string EndDate { get; set; }

    public int Priority { get; set; }

    public List<YamlPlayoutInstruction> Playout { get; set; } = [];
}
