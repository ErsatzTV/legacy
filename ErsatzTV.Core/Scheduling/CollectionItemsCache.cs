using System.Collections.Concurrent;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Scheduling;

// one instance per playout build; repositories are scoped and can outlive a build (blazor circuits),
// so they must not hold this themselves
public sealed class CollectionItemsCache
{
    private readonly ConcurrentDictionary<int, Lazy<Task<List<MediaItem>>>> _smartCollectionItems = new();

    public async Task<List<MediaItem>> GetSmartCollectionItems(
        int smartCollectionId,
        Func<Task<List<MediaItem>>> load)
    {
        Lazy<Task<List<MediaItem>>> lazy = _smartCollectionItems.GetOrAdd(
            smartCollectionId,
            _ => new Lazy<Task<List<MediaItem>>>(load));

        // callers mutate the lists they receive (e.g. RemoveAll in GetMultiCollectionCollections)
        return [..await lazy.Value];
    }
}
