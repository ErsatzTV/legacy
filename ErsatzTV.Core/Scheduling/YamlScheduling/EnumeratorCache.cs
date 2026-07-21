using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.BlockScheduling;
using ErsatzTV.Core.Scheduling.Engine;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling;

public class EnumeratorCache(IMediaCollectionRepository mediaCollectionRepository, ILogger logger)
{
    private readonly Dictionary<string, IMediaCollectionEnumerator> _enumerators = new();
    private readonly Dictionary<string, List<MediaItem>> _mediaItems = new();
    private readonly Dictionary<PlaylistKey, List<MediaItem>> _playlistMediaItems = new();

    public System.Collections.Generic.HashSet<string> MissingContentKeys { get; } = [];

    public List<MediaItem> MediaItemsForContent(string contentKey) =>
        _mediaItems.TryGetValue(contentKey, out List<MediaItem> items) ? items : [];

    public List<MediaItem> PlaylistMediaItemsForContent(string contentKey, CollectionKey collectionKey) =>
        _playlistMediaItems.TryGetValue(new PlaylistKey(contentKey, collectionKey), out List<MediaItem> items)
            ? items
            : [];

    public async Task<Option<IMediaCollectionEnumerator>> GetCachedEnumeratorForContent(
        YamlPlayoutContext context,
        string contentKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentKey))
        {
            return Option<IMediaCollectionEnumerator>.None;
        }

        if (!_enumerators.TryGetValue(contentKey, out IMediaCollectionEnumerator enumerator))
        {
            Option<IMediaCollectionEnumerator> maybeEnumerator =
                await GetEnumeratorForContent(context, contentKey, cancellationToken);

            if (maybeEnumerator.IsNone)
            {
                return Option<IMediaCollectionEnumerator>.None;
            }

            foreach (IMediaCollectionEnumerator e in maybeEnumerator)
            {
                enumerator = e;
                _enumerators.Add(contentKey, enumerator);
            }
        }

        return Some(enumerator);
    }

    private async Task<Option<IMediaCollectionEnumerator>> GetEnumeratorForContent(
        YamlPlayoutContext context,
        string contentKey,
        CancellationToken cancellationToken)
    {
        int index = context.Definition.Content.FindIndex(c => c.Key == contentKey);
        if (index < 0)
        {
            return Option<IMediaCollectionEnumerator>.None;
        }

        List<MediaItem> items = [];

        YamlPlayoutContentItem content = context.Definition.Content[index];
        switch (content)
        {
            case YamlPlayoutContentSearchItem search:
                items = await mediaCollectionRepository.GetSmartCollectionItems(search.Query, string.Empty, cancellationToken);
                break;
            case YamlPlayoutContentShowItem show:
                items = await mediaCollectionRepository.GetShowItemsByShowGuids(
                    show.Guids.Map(g => $"{g.Source}://{g.Value}").ToList());
                break;
            case YamlPlayoutContentCollectionItem collection:
                items = await mediaCollectionRepository.GetCollectionItemsByName(
                    collection.Collection,
                    cancellationToken);
                break;
            case YamlPlayoutContentSmartCollectionItem smartCollection:
                items = await mediaCollectionRepository.GetSmartCollectionItemsByName(
                    smartCollection.SmartCollection,
                    cancellationToken);
                break;
            case YamlPlayoutContentMultiCollectionItem multiCollection:
                items = await mediaCollectionRepository.GetMultiCollectionItemsByName(
                    multiCollection.MultiCollection,
                    cancellationToken);
                break;

            // playlist is handled later
        }

        _mediaItems[content.Key] = items;

        var state = new CollectionEnumeratorState { Seed = context.Playout.Seed + index, Index = 0 };

        // marathon is a special case that needs to be handled on its own
        if (content is YamlPlayoutContentMarathonItem marathon)
        {
            var helper = new MarathonHelper(mediaCollectionRepository);
            var guids = new Dictionary<string, List<string>>();
            foreach (var guid in marathon.Guids)
            {
                if (!guids.TryGetValue(guid.Source, out List<string> value))
                {
                    value = [];
                    guids.Add(guid.Source, value);
                }

                value.Add(guid.Value);
            }

            if (!Enum.TryParse(marathon.ItemOrder, true, out PlaybackOrder itemPlaybackOrder))
            {
                itemPlaybackOrder = PlaybackOrder.Shuffle;
            }

            Option<PlaylistContentResult> maybeResult = await helper.GetEnumerator(
                guids,
                marathon.Searches,
                marathon.GroupBy,
                marathon.ShuffleGroups,
                itemPlaybackOrder,
                marathon.PlayAllItems,
                state,
                cancellationToken);

            foreach (PlaylistContentResult result in maybeResult)
            {
                foreach ((CollectionKey collectionKey, List<MediaItem> mediaItems) in result.Content)
                {
                    _playlistMediaItems.Add(new PlaylistKey(contentKey, collectionKey), mediaItems);
                }

                return result.PlaylistEnumerator;
            }
        }

        // playlist is a special case that needs to be handled on its own
        if (content is YamlPlayoutContentPlaylistItem playlist)
        {
            if (!string.IsNullOrWhiteSpace(playlist.Order) && !string.Equals(
                    playlist.Order,
                    "none",
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Ignoring playback order {Order} for playlist {Playlist}",
                    playlist.Order,
                    playlist.Playlist);
            }

            Dictionary<PlaylistItem, List<MediaItem>> itemMap =
                await mediaCollectionRepository.GetPlaylistItemMap(
                    playlist.PlaylistGroup,
                    playlist.Playlist,
                    cancellationToken);

            foreach ((PlaylistItem playlistItem, List<MediaItem> mediaItems) in itemMap)
            {
                _playlistMediaItems.Add(
                    new PlaylistKey(contentKey, CollectionKey.ForPlaylistItem(playlistItem)),
                    mediaItems);
            }

            return await PlaylistEnumerator.Create(
                mediaCollectionRepository,
                itemMap,
                state,
                shufflePlaylistItems: false,
                batchSize: Option<int>.None,
                randomStartPoint: false,
                cancellationToken);
        }

        switch (Enum.Parse<PlaybackOrder>(content.Order, true))
        {
            case PlaybackOrder.Chronological:
                return new ChronologicalMediaCollectionEnumerator(items, state);
            case PlaybackOrder.Shuffle:
                bool keepMultiPartEpisodesTogether = content.MultiPart;
                List<GroupedMediaItem> groupedMediaItems = keepMultiPartEpisodesTogether
                    ? MultiPartEpisodeGrouper.GroupMediaItems(items, false)
                    : items.Map(mi => new GroupedMediaItem(mi, null)).ToList();
                return new BlockPlayoutShuffledMediaCollectionEnumerator(groupedMediaItems, state);
        }

        return Option<IMediaCollectionEnumerator>.None;
    }

    private record PlaylistKey(string ContentKey, CollectionKey CollectionKey);
}
