using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Newtonsoft.Json;

namespace ErsatzTV.Core.Scheduling;

internal static class HistoryDetails
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    public static string KeyForBlockItem(BlockItem blockItem)
    {
        dynamic key = new
        {
            blockItem.BlockId,
            blockItem.PlaybackOrder,
            blockItem.CollectionType,
            blockItem.CollectionId,
            blockItem.MultiCollectionId,
            blockItem.SmartCollectionId,
            blockItem.MediaItemId,
            blockItem.SearchQuery
        };

        return JsonConvert.SerializeObject(key, Formatting.None, JsonSettings);
    }

    public static string KeyForYamlContent(YamlPlayoutContentItem contentItem)
    {
        var key = new Dictionary<string, object>
        {
            { "Key", contentItem.Key },
            { "Order", contentItem.Order }
        };

        // we need to ignore history when any of these properties change
        if (contentItem is YamlPlayoutContentMarathonItem marathonItem)
        {
            key["ItemOrder"] = marathonItem.ItemOrder;
            //key.ShuffleGroups = marathonItem.ShuffleGroups;
            key["GroupBy"] = marathonItem.GroupBy;
            //key.PlayAllItems = marathonItem.PlayAllItems;
        }

        return JsonConvert.SerializeObject(key, Formatting.None, JsonSettings);
    }

    public static string KeyForSchedulingContent(string key, PlaybackOrder playbackOrder)
    {
        var historyKey = new Dictionary<string, object>
        {
            { "Key", key },
            { "Order", playbackOrder.ToString() }
        };

        return JsonConvert.SerializeObject(historyKey, Formatting.None, JsonSettings);
    }

    public static string KeyForSchedulingMarathonContent(string key, PlaybackOrder itemPlaybackOrder, string groupBy)
    {
        var historyKey = new Dictionary<string, object>
        {
            { "Key", key },
            { "Order", nameof(PlaybackOrder.None) },

            // we need to ignore history when any of these properties change
            { "ItemOrder", itemPlaybackOrder.ToString() },
            { "GroupBy", groupBy }
        };

        return JsonConvert.SerializeObject(historyKey, Formatting.None, JsonSettings);
    }

    public static string KeyForSchedulingPlaylistContent(string key)
    {
        var historyKey = new Dictionary<string, object>
        {
            { "Key", key },
            { "Order", nameof(PlaybackOrder.None) }
        };

        return JsonConvert.SerializeObject(historyKey, Formatting.None, JsonSettings);
    }

    public static string KeyForCollectionKey(CollectionKey collectionKey)
    {
        dynamic key = new
        {
            collectionKey.CollectionType,
            collectionKey.CollectionId,
            collectionKey.MultiCollectionId,
            collectionKey.SmartCollectionId,
            collectionKey.MediaItemId,
            collectionKey.PlaylistId,
            collectionKey.SearchQuery,
            collectionKey.FakeCollectionKey
        };

        return JsonConvert.SerializeObject(key, Formatting.None, JsonSettings);
    }

    public static string ForBreakContent(DecoBreakContent breakContent)
    {
        dynamic key = new
        {
            DecoBreakContentId = breakContent.Id,
            PlaybackOrder = PlaybackOrder.Shuffle,
            breakContent.CollectionType,
            breakContent.CollectionId,
            breakContent.MultiCollectionId,
            breakContent.SmartCollectionId,
            breakContent.MediaItemId,
            breakContent.PlaylistId
        };

        return JsonConvert.SerializeObject(key, Formatting.None, JsonSettings);
    }

    public static string ForDefaultFiller(Deco deco)
    {
        dynamic key = new
        {
            DecoId = deco.Id,
            PlaybackOrder = PlaybackOrder.Shuffle,
            CollectionType = deco.DefaultFillerCollectionType,
            CollectionId = deco.DefaultFillerCollectionId,
            MultiCollectionId = deco.DefaultFillerMultiCollectionId,
            SmartCollectionId = deco.DefaultFillerSmartCollectionId
        };

        return JsonConvert.SerializeObject(key, Formatting.None, JsonSettings);
    }

    public static string ForMediaItem(MediaItem mediaItem)
    {
        Details details = mediaItem switch
        {
            Episode e => ForEpisode(e),
            Movie m => ForMovie(m),
            _ => new Details(mediaItem.Id, null, null, null)
        };

        return JsonConvert.SerializeObject(details, Formatting.None, JsonSettings);
    }

    public static void MoveToNextItem(
        List<MediaItem> collectionItems,
        string detailsString,
        IMediaCollectionEnumerator enumerator,
        PlaybackOrder playbackOrder,
        bool current = false)
    {
        if (playbackOrder is PlaybackOrder.Random)
        {
            return;
        }

        Option<MediaItem> maybeMatchedItem = Option<MediaItem>.None;

        // the index must agree with the list that the enumerator sorted.
        // that enumerator removes season 0, so this list must remove it too.
        List<MediaItem> copy = enumerator is SeasonEpisodeMediaCollectionEnumerator
            ? SeasonEpisodeMediaCollectionEnumerator.Playable(collectionItems)
            : collectionItems.ToList();

        if (copy.Count == 0)
        {
            return;
        }

        Details details = JsonConvert.DeserializeObject<Details>(detailsString);

        MediaItem placeholder = null;

        // try for an exact match first
        if (details.MediaItemId != null)
        {
            maybeMatchedItem = copy.Find(mi => mi.Id == details.MediaItemId);
        }

        if (maybeMatchedItem.IsNone && details.SeasonNumber.HasValue && details.EpisodeNumber.HasValue)
        {
            int season = details.SeasonNumber.Value;
            int episode = details.EpisodeNumber.Value;

            maybeMatchedItem = Optional(copy.Find(ci => MatchSeasonAndEpisode(ci, season, episode)));

            if (maybeMatchedItem.IsNone)
            {
                var fakeItem = new Episode
                {
                    Season = new Season { SeasonNumber = season },
                    EpisodeMetadata =
                    [
                        new EpisodeMetadata
                        {
                            EpisodeNumber = episode,
                            ReleaseDate = details.ReleaseDate
                        }
                    ]
                };

                copy.Add(fakeItem);
                maybeMatchedItem = fakeItem;
                placeholder = fakeItem;
            }
        }
        else if (maybeMatchedItem.IsNone && playbackOrder is PlaybackOrder.Chronological &&
                 details.ReleaseDate.HasValue)
        {
            maybeMatchedItem = Optional(copy.Find(ci => MatchReleaseDate(ci, details.ReleaseDate.Value)));

            if (maybeMatchedItem.IsNone)
            {
                var fakeItem = new Movie { MovieMetadata = [new MovieMetadata { ReleaseDate = details.ReleaseDate }] };
                copy.Add(fakeItem);
                maybeMatchedItem = fakeItem;
                placeholder = fakeItem;
            }
        }

        foreach (MediaItem matchedItem in maybeMatchedItem)
        {
            IComparer<MediaItem> comparer = playbackOrder switch
            {
                PlaybackOrder.Chronological => new ChronologicalMediaComparer(),
                _ => new SeasonEpisodeMediaComparer()
            };

            copy.Sort(comparer);

            int index = copy.IndexOf(matchedItem);
            if (placeholder is not null)
            {
                // the enumerator does not hold the placeholder.
                // its position is where the next real item is, and that position can be past the end.
                copy.Remove(placeholder);
                index %= copy.Count;
            }

            var state = new CollectionEnumeratorState
            {
                Seed = enumerator.State.Seed,
                Index = index
            };
            enumerator.ResetState(state);

            if (!current)
            {
                enumerator.MoveNext(Option<DateTimeOffset>.None);
            }
        }
    }

    private static bool MatchReleaseDate(MediaItem mediaItem, DateTime releaseDate) =>
        mediaItem switch
        {
            Movie m => m.MovieMetadata.Any(mm => mm.ReleaseDate == releaseDate),
            Episode e => e.EpisodeMetadata.Any(em => em.ReleaseDate == releaseDate),
            //MusicVideo mv => mv.MusicVideoMetadata.Any(mvm => mvm.ReleaseDate == releaseDate),
            OtherVideo ov => ov.OtherVideoMetadata.Any(ovm => ovm.ReleaseDate == releaseDate),
            _ => false
        };

    private static bool MatchSeasonAndEpisode(MediaItem mediaItem, int seasonNumber, int episodeNumber) =>
        mediaItem switch
        {
            Episode e => e.Season.SeasonNumber == seasonNumber &&
                         e.EpisodeMetadata.Any(em => em.EpisodeNumber == episodeNumber),
            _ => false
        };

    private static Details ForEpisode(Episode e)
    {
        int? episodeNumber = null;
        DateTime? releaseDate = null;
        foreach (EpisodeMetadata episodeMetadata in e.EpisodeMetadata.HeadOrNone())
        {
            episodeNumber = episodeMetadata.EpisodeNumber;
            releaseDate = episodeMetadata.ReleaseDate;
        }

        return new Details(e.Id, releaseDate, e.Season.SeasonNumber, episodeNumber);
    }

    private static Details ForMovie(Movie m)
    {
        DateTime? releaseDate = null;
        foreach (MovieMetadata movieMetadata in m.MovieMetadata.HeadOrNone())
        {
            releaseDate = movieMetadata.ReleaseDate;
        }

        return new Details(m.Id, releaseDate, null, null);
    }

    public record Details(int? MediaItemId, DateTime? ReleaseDate, int? SeasonNumber, int? EpisodeNumber);
}
