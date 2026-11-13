using System.Collections.Concurrent;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Scheduling;

// one per playout build; scoped repositories can outlive a build (blazor circuits), so they must not hold this
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

        // copy: callers change the returned list (e.g. GetMultiCollectionCollections)
        return [..await lazy.Value];
    }
}
