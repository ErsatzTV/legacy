using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Core.Scheduling.Engine;

public class PlaylistHelper(IMediaCollectionRepository mediaCollectionRepository)
{
    public async Task<Option<PlaylistContentResult>> GetEnumerator(
        Dictionary<string, EnumeratorDetails> enumerators,
        Dictionary<string, ImmutableList<MediaItem>> enumeratorMediaItems,
        Dictionary<string, int> playlistItems,
        CollectionEnumeratorState state,
        CancellationToken cancellationToken)
    {
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = [];

        int playlistIndex = 0;
        var allKeys = playlistItems.Keys.ToList();
        for (var index = 0; index < allKeys.Count; index++)
        {
            string key = allKeys[index];
            ImmutableList<MediaItem> mediaItems = null;
            if (!enumerators.TryGetValue(key, out EnumeratorDetails enumeratorDetails) ||
                !enumeratorMediaItems.TryGetValue(key, out mediaItems))
            {
                Console.WriteLine($"Something is wrong with the playlist with key {key}");
                Console.WriteLine($"details: {(enumeratorDetails is null ? "null" : "not null")}");
                Console.WriteLine($"items: {(mediaItems?.Count ?? -1)}");

                continue;
            }

            int count = playlistItems[key];
            for (var i = 0; i < count; i++)
            {
                PlaylistItem playlistItem = new()
                {
                    Index = playlistIndex,

                    CollectionType = CollectionType.FakePlaylistItem,
                    CollectionId = playlistIndex,

                    PlayAll = false,
                    PlaybackOrder = enumeratorDetails.PlaybackOrder,

                    IncludeInProgramGuide = true
                };

                itemMap.Add(playlistItem, mediaItems.ToList());
                playlistIndex++;
            }
        }

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            mediaCollectionRepository,
            itemMap,
            state,
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            cancellationToken);

        return new PlaylistContentResult(
            enumerator,
            itemMap.ToImmutableDictionary(x => CollectionKey.ForPlaylistItem(x.Key), x => x.Value));
    }
}
