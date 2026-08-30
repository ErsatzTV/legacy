using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutInstruction
{
    public virtual bool ChangesIndex => false;

    public string Content { get; set; }

    [YamlMember(Alias = "filler_kind", ApplyNamingConventions = false)]
    public string FillerKind { get; set; }

    [YamlMember(Alias = "custom_title", ApplyNamingConventions = false)]
    public string CustomTitle { get; set; }

    [YamlMember(Alias = "disable_watermarks", ApplyNamingConventions = false)]
    public bool DisableWatermarks { get; set; } = false;

    [YamlIgnore]
    public string SequenceKey { get; set; }

    [YamlIgnore]
    public Guid SequenceGuid { get; set; }

    [YamlIgnore]
    public int SequenceIndex { get; set; }

    [YamlIgnore]
    public bool SequenceShuffled { get; set; }

    [YamlIgnore]
    public string SequenceFingerprint { get; set; }

    public YamlPlayoutInstruction Clone() => (YamlPlayoutInstruction)MemberwiseClone();
}
