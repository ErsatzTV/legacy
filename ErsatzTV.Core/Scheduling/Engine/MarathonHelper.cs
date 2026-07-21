using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Core.Scheduling.Engine;

public class MarathonHelper(IMediaCollectionRepository mediaCollectionRepository)
{
    public async Task<Option<PlaylistEnumerator>> GetEnumerator(
        List<MediaItem> mediaItems,
        MarathonGroupBy marathonGroupBy,
        bool marathonShuffleGroups,
        bool marathonShuffleItems,
        Option<int> marathonBatchSize,
        CollectionEnumeratorState state,
        CancellationToken cancellationToken)
    {
        List<IGrouping<GroupKey, MediaItem>> groups = [];

        PlaybackOrder itemPlaybackOrder;

        // group by show
        switch (marathonGroupBy)
        {
            case MarathonGroupBy.Show:
                groups.AddRange(mediaItems.GroupBy(MediaItemKeyByShow));
                itemPlaybackOrder = marathonShuffleItems ? PlaybackOrder.Shuffle : PlaybackOrder.SeasonEpisode;
                break;
            case MarathonGroupBy.Season:
                groups.AddRange(mediaItems.GroupBy(MediaItemKeyBySeason));
                itemPlaybackOrder = marathonShuffleItems ? PlaybackOrder.Shuffle : PlaybackOrder.SeasonEpisode;
                break;
            case MarathonGroupBy.Artist:
                groups.AddRange(mediaItems.GroupBy(MediaItemKeyByArtist));
                itemPlaybackOrder = marathonShuffleItems ? PlaybackOrder.Shuffle : PlaybackOrder.Chronological;
                break;
            case MarathonGroupBy.Album:
                groups.AddRange(mediaItems.GroupBy(MediaItemKeyByAlbum));
                itemPlaybackOrder = marathonShuffleItems ? PlaybackOrder.Shuffle : PlaybackOrder.Chronological;
                break;
            case MarathonGroupBy.Director:
                groups.AddRange(mediaItems.GroupBy(MediaItemKeyByDirector));
                itemPlaybackOrder = marathonShuffleItems ? PlaybackOrder.Shuffle : PlaybackOrder.Chronological;
                break;
            default:
                return Option<PlaylistEnumerator>.None;
        }

        Dictionary<PlaylistItem, List<MediaItem>> itemMap = [];

        for (var index = 0; index < groups.Count; index++)
        {
            IGrouping<GroupKey, MediaItem> group = groups[index];
            PlaylistItem playlistItem = GroupToPlaylistItem(
                index,
                marathonBatchSize.IsNone,
                itemPlaybackOrder,
                group);
            itemMap.Add(playlistItem, group.ToList());
        }

        return await PlaylistEnumerator.Create(
            mediaCollectionRepository,
            itemMap,
            state,
            marathonShuffleGroups,
            marathonBatchSize,
            randomStartPoint: false,
            cancellationToken);
    }

    public async Task<Option<PlaylistContentResult>> GetEnumerator(
        Dictionary<string, List<string>> guids,
        List<string> searches,
        string groupBy,
        bool shuffleGroups,
        PlaybackOrder itemPlaybackOrder,
        bool playAllItems,
        CollectionEnumeratorState state,
        CancellationToken cancellationToken)
    {
        var allMediaItems = new List<MediaItem>();

        // grab items from each show guid
        foreach (string showGuid in guids.SelectMany(g => g.Value.Select(v => $"{g.Key}://{v}")))
        {
            allMediaItems.AddRange(await mediaCollectionRepository.GetShowItemsByShowGuids([showGuid]));
        }

        // grab items from each search
        foreach (string query in searches)
        {
            allMediaItems.AddRange(await mediaCollectionRepository.GetSmartCollectionItems(query, string.Empty, cancellationToken));
        }

        List<IGrouping<GroupKey, MediaItem>> groups = [];

        // group by show
        if (string.Equals(groupBy, "show", StringComparison.OrdinalIgnoreCase))
        {
            groups.AddRange(allMediaItems.GroupBy(MediaItemKeyByShow));
        }
        // group by season
        else if (string.Equals(groupBy, "season", StringComparison.OrdinalIgnoreCase))
        {
            groups.AddRange(allMediaItems.GroupBy(MediaItemKeyBySeason));
        }
        // group by artist
        else if (string.Equals(groupBy, "artist", StringComparison.OrdinalIgnoreCase))
        {
            groups.AddRange(allMediaItems.GroupBy(MediaItemKeyByArtist));
        }
        // group by album
        else if (string.Equals(groupBy, "album", StringComparison.OrdinalIgnoreCase))
        {
            groups.AddRange(allMediaItems.GroupBy(MediaItemKeyByAlbum));
        }
        // group by (first) director
        else if (string.Equals(groupBy, "director", StringComparison.OrdinalIgnoreCase))
        {
            groups.AddRange(allMediaItems.GroupBy(MediaItemKeyByDirector));
        }

        Dictionary<PlaylistItem, List<MediaItem>> itemMap = [];

        for (var index = 0; index < groups.Count; index++)
        {
            IGrouping<GroupKey, MediaItem> group = groups[index];
            PlaylistItem playlistItem = GroupToPlaylistItem(index, playAllItems, itemPlaybackOrder, group);
            itemMap.Add(playlistItem, group.ToList());
        }

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            mediaCollectionRepository,
            itemMap,
            state,
            shuffleGroups,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            cancellationToken);

        return new PlaylistContentResult(
            enumerator,
            itemMap.ToImmutableDictionary(x => CollectionKey.ForPlaylistItem(x.Key), x => x.Value));
    }

    private static GroupKey MediaItemKeyByShow(MediaItem mediaItem) =>
        mediaItem switch
        {
            Episode e => new GroupKey(
                CollectionType.TelevisionShow,
                null,
                null,
                null,
                e.Season?.ShowId ?? 0),
            _ => new GroupKey(CollectionType.TelevisionShow, null, null, null, 0)
        };

    private static GroupKey MediaItemKeyBySeason(MediaItem mediaItem) =>
        mediaItem switch
        {
            Episode e => new GroupKey(
                CollectionType.TelevisionSeason,
                null,
                null,
                null,
                e.SeasonId),
            _ => new GroupKey(CollectionType.TelevisionSeason, null, null, null, 0)
        };

    private static GroupKey MediaItemKeyByArtist(MediaItem mediaItem) =>
        mediaItem switch
        {
            MusicVideo mv => new GroupKey(
                CollectionType.Artist,
                null,
                null,
                null,
                mv.ArtistId),
            _ => new GroupKey(CollectionType.Artist, null, null, null, 0)
        };

    private static GroupKey MediaItemKeyByAlbum(MediaItem mediaItem) =>
        mediaItem switch
        {
            Song s => new GroupKey(
                CollectionType.Collection,
                s.SongMetadata.HeadOrNone().Map(sm => sm.Album.GetStableHashCode()).IfNone(0),
                null,
                null,
                null),
            MusicVideo mv => new GroupKey(
                CollectionType.Collection,
                mv.MusicVideoMetadata.HeadOrNone()
                    .Map(mvm => $"{mv.ArtistId}-${mvm.Album}".GetStableHashCode()).IfNone(0),
                null,
                null,
                null),
            _ => new GroupKey(CollectionType.Collection, 0, null, null, null)
        };

    private static GroupKey MediaItemKeyByDirector(MediaItem mediaItem) =>
        mediaItem switch
        {
            Movie m => new GroupKey(
                CollectionType.Collection,
                m.MovieMetadata.HeadOrNone().Map(mm => FirstDirectorHashCode(mm.Directors)).IfNone(0),
                null,
                null,
                null),
            Episode e => new GroupKey(
                CollectionType.Collection,
                e.EpisodeMetadata.HeadOrNone().Map(em => FirstDirectorHashCode(em.Directors)).IfNone(0),
                null,
                null,
                null),
            MusicVideo mv => new GroupKey(
                CollectionType.Collection,
                mv.MusicVideoMetadata.HeadOrNone().Map(mvm => FirstDirectorHashCode(mvm.Directors)).IfNone(0),
                null,
                null,
                null),
            OtherVideo ov => new GroupKey(
                CollectionType.Collection,
                ov.OtherVideoMetadata.HeadOrNone().Map(ovm => FirstDirectorHashCode(ovm.Directors)).IfNone(0),
                null,
                null,
                null),
            _ => new GroupKey(CollectionType.Collection, 0, null, null, null)
        };

    private static int FirstDirectorHashCode(List<Director> directors) =>
        directors.HeadOrNone().Select(director => director.Name.GetStableHashCode()).FirstOrDefault();

    private static PlaylistItem GroupToPlaylistItem(
        int index,
        bool playAllItems,
        PlaybackOrder playbackOrder,
        IGrouping<GroupKey, MediaItem> group) =>
        new()
        {
            Index = index,

            CollectionType = group.Key.CollectionType,
            CollectionId = group.Key.CollectionId,
            MultiCollectionId = group.Key.MultiCollectionId,
            SmartCollectionId = group.Key.SmartCollectionId,
            MediaItemId = group.Key.MediaItemId,

            PlayAll = playAllItems,
            PlaybackOrder = playbackOrder,

            IncludeInProgramGuide = true
        };

    private record GroupKey(
        CollectionType CollectionType,
        int? CollectionId,
        int? MultiCollectionId,
        int? SmartCollectionId,
        int? MediaItemId);
}
