using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;

namespace ErsatzTV.Core.Tests.Fakes;

public class FakeMediaCollectionRepository : IMediaCollectionRepository
{
    private readonly Map<int, List<MediaItem>> _data;
    private readonly Map<int, Dictionary<PlaylistItem, List<MediaItem>>> _playlists;

    public FakeMediaCollectionRepository(
        Map<int, List<MediaItem>> data,
        Map<int, Dictionary<PlaylistItem, List<MediaItem>>> playlists = default)
    {
        _data = data;
        _playlists = playlists;
    }

    public Task<Dictionary<PlaylistItem, List<MediaItem>>> GetPlaylistItemMap(
        int playlistId,
        CancellationToken cancellationToken) =>
        _playlists[playlistId].AsTask();

    public Task<Dictionary<PlaylistItem, List<MediaItem>>> GetPlaylistItemMap(
        string groupName,
        string name,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Dictionary<PlaylistItem, List<MediaItem>>> GetPlaylistItemMap(
        Playlist playlist,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Option<Collection>> GetCollectionWithCollectionItemsUntracked(int id) =>
        throw new NotSupportedException();

    public Task<List<MediaItem>> GetItems(int id) => _data[id].ToList().AsTask();
    public Task<List<MediaItem>> GetCollectionItemsByName(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<List<MediaItem>> GetMultiCollectionItems(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<List<MediaItem>> GetMultiCollectionItemsByName(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<List<MediaItem>> GetSmartCollectionItems(int id, CancellationToken cancellationToken) => _data[id].ToList().AsTask();
    public Task<List<MediaItem>> GetSmartCollectionItemsByName(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<List<MediaItem>> GetSmartCollectionItems(
        string query,
        string smartCollectionName,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<List<MediaItem>> GetRerunCollectionItems(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<List<MediaItem>> GetShowItemsByShowGuids(List<string> guids) => throw new NotSupportedException();
    public Task<List<MediaItem>> GetPlaylistItems(int id, CancellationToken cancellationToken) =>
        _playlists[id].Values.SelectMany(items => items).DistinctBy(mi => mi.Id).ToList().AsTask();
    public Task<List<Movie>> GetMovie(int id) => throw new NotSupportedException();
    public Task<List<Episode>> GetEpisode(int id) => throw new NotSupportedException();
    public Task<List<MusicVideo>> GetMusicVideo(int id) => throw new NotSupportedException();
    public Task<List<OtherVideo>> GetOtherVideo(int id) => throw new NotSupportedException();
    public Task<List<Song>> GetSong(int id) => throw new NotSupportedException();
    public Task<List<Image>> GetImage(int id) => throw new NotSupportedException();
    public Task<List<RemoteStream>> GetRemoteStream(int id) => throw new NotSupportedException();

    public Task<List<CollectionWithItems>> GetMultiCollectionCollections(int id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<List<CollectionWithItems>> GetFakeMultiCollectionCollections(
        int? collectionId,
        int? smartCollectionId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<List<int>> PlayoutIdsUsingCollection(int collectionId) => throw new NotSupportedException();

    public Task<List<int>> PlayoutIdsUsingMultiCollection(int multiCollectionId) =>
        throw new NotSupportedException();

    public Task<List<int>> PlayoutIdsUsingSmartCollection(int smartCollectionId) =>
        throw new NotSupportedException();

    public Task<List<int>> PlayoutIdsUsingRerunCollection(int rerunCollectionId) =>
        throw new NotSupportedException();

    public Task<bool> IsCustomPlaybackOrder(int collectionId) => false.AsTask();
    public Task<Option<string>> GetNameFromKey(CollectionKey emptyCollection, CancellationToken cancellationToken) => Option<string>.None.AsTask();

    public List<CollectionWithItems> GroupIntoFakeCollections(List<MediaItem> items, string fakeKey = null)
    {
        return [new CollectionWithItems(1, 0, fakeKey, items, true, PlaybackOrder.Shuffle, false)];
    }
}
