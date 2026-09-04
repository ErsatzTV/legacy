namespace ErsatzTV.Core.Domain.Scheduling;

public class PlayoutHistory
{
    public int Id { get; set; }

    public int PlayoutId { get; set; }
    public Playout Playout { get; set; }

    public int? BlockId { get; set; }
    public Block Block { get; set; }
    public PlaybackOrder PlaybackOrder { get; set; }
    public int Index { get; set; }

    // the cycle that Index counts in. A row from before this column has null.
    public int? Seed { get; set; }

    // something that uniquely identifies the collection within the block
    public string Key { get; set; }

    // something that uniquely identifies a child collection within the parent collection
    // e.g. for a playlist
    public string ChildKey { get; set; }

    public bool IsCurrentChild { get; set; }

    // last occurence of an item from this collection in the playout
    public DateTime When { get; set; }

    // used to efficiently ignore/remove "still active" history items
    public DateTime Finish { get; set; }

    // details about the item
    public string Details { get; set; }

    public PlayoutHistory Clone() =>
        new()
        {
            PlayoutId = PlayoutId,
            BlockId = BlockId,
            PlaybackOrder = PlaybackOrder,
            Index = Index,
            Seed = Seed,
            Key = Key,
            ChildKey = ChildKey,
            IsCurrentChild = IsCurrentChild,
            When = When,
            Finish = Finish,
            Details = Details
        };
}
