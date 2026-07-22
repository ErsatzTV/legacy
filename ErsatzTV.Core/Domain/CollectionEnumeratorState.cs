namespace ErsatzTV.Core.Domain;

public class CollectionEnumeratorState
{
    public int Seed { get; set; }
    public int Index { get; set; }
    public bool Started { get; set; }
    public CollectionEnumeratorState Clone() => new() { Seed = Seed, Index = Index, Started = Started };
}
