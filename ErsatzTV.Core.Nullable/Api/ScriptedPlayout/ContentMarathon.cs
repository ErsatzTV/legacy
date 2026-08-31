using System.ComponentModel;

namespace ErsatzTV.Core.Api.ScriptedPlayout;

public record ContentMarathon
{
    [Description("Unique name used to reference this content throughout the scripted schedule")]
    public required string Key { get; set; }

    [Description(
        "Tells the scheduler how to group the combined content (returned from all guids and searches). Valid values are show, season, artist, album and director.")]
    public required string GroupBy { get; set; }

    [Description("Playback order within each group; only chronological and shuffle are currently supported")]
    public string ItemOrder { get; set; } = "shuffle";

    [Description("List of external content identifiers")]
    public Dictionary<string, List<string>> Guids { get; set; } = [];

    [Description("List of search queries")]
    public List<string> Searches { get; set; } = [];

    [Description(
        "When true, will add every item from a group before moving to the next group. When false, will play one item from a group before moving to the next group.")]
    public bool PlayAllItems { get; set; }

    [Description(
        "When true, will randomize the order of groups. When false, will cycle through groups in a fixed order.")]
    public bool ShuffleGroups { get; set; }
}
