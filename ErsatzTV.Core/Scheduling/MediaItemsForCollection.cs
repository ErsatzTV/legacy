using System.Diagnostics.CodeAnalysis;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Core.Scheduling;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public static class MediaItemsForCollection
{
    public static async Task<List<MediaItem>> Collect(
        IMediaCollectionRepository mediaCollectionRepository,
        ITelevisionRepository televisionRepository,
        IArtistRepository artistRepository,
        CollectionKey collectionKey,
        CollectionItemsCache cache,
        CancellationToken cancellationToken)
    {
        var result = new List<MediaItem>();

        switch (collectionKey.CollectionType)
        {
            case CollectionType.Collection:
                result.AddRange(await mediaCollectionRepository.GetItems(collectionKey.CollectionId ?? 0));
                break;
            case CollectionType.TelevisionShow:
                result.AddRange(await televisionRepository.GetShowItems(collectionKey.MediaItemId ?? 0));
                break;
            case CollectionType.TelevisionSeason:
                result.AddRange(await televisionRepository.GetSeasonItems(collectionKey.MediaItemId ?? 0));
                break;
            case CollectionType.Artist:
                result.AddRange(await artistRepository.GetArtistItems(collectionKey.MediaItemId ?? 0));
                break;
            case CollectionType.MultiCollection:
                result.AddRange(
                    await mediaCollectionRepository.GetMultiCollectionItems(
                        collectionKey.MultiCollectionId ?? 0,
                        cache,
                        cancellationToken));
                break;
            case CollectionType.SmartCollection:
                result.AddRange(
                    await mediaCollectionRepository.GetSmartCollectionItems(
                        collectionKey.SmartCollectionId ?? 0,
                        cache,
                        cancellationToken));
                break;
            case CollectionType.RerunFirstRun or CollectionType.RerunRerun:
                result.AddRange(
                    await mediaCollectionRepository.GetRerunCollectionItems(
                        collectionKey.RerunCollectionId ?? 0,
                        cancellationToken));
                break;
            case CollectionType.Playlist:
                result.AddRange(
                    await mediaCollectionRepository.GetPlaylistItems(collectionKey.PlaylistId ?? 0, cancellationToken));
                break;
            case CollectionType.SearchQuery:
                result.AddRange(
                    await mediaCollectionRepository.GetSmartCollectionItems(
                        collectionKey.SearchQuery,
                        string.Empty,
                        cancellationToken));
                break;
            case CollectionType.Movie:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetMovie(mediaItemId));
                }

                break;
            case CollectionType.Episode:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetEpisode(mediaItemId));
                }

                break;
            case CollectionType.MusicVideo:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetMusicVideo(mediaItemId));
                }

                break;
            case CollectionType.OtherVideo:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetOtherVideo(mediaItemId));
                }

                break;
            case CollectionType.Song:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetSong(mediaItemId));
                }

                break;
            case CollectionType.Image:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetImage(mediaItemId));
                }

                break;
            case CollectionType.RemoteStream:
                foreach (int mediaItemId in Optional(collectionKey.MediaItemId))
                {
                    result.AddRange(await mediaCollectionRepository.GetRemoteStream(mediaItemId));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(collectionKey));
        }

        return result.DistinctBy(x => x.Id).ToList();
    }
}
