using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.BlockScheduling;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TimeSpanParserUtil;

namespace ErsatzTV.Core.Scheduling.Engine;

public class SchedulingEngine(
    IMediaCollectionRepository mediaCollectionRepository,
    IGraphicsElementRepository graphicsElementRepository,
    IChannelRepository channelRepository,
    ILogger<SchedulingEngine> logger)
    : ISchedulingEngine
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly Dictionary<string, Option<GraphicsElement>> _graphicsElementCache = new();
    private readonly Dictionary<string, Option<ChannelWatermark>> _watermarkCache = new();
    private readonly Dictionary<string, EnumeratorDetails> _enumerators = new();
    private readonly Dictionary<string, ImmutableList<MediaItem>> _enumeratorMediaItems = new();
    private readonly SchedulingEngineState _state = new(0);
    private PlayoutReferenceData _referenceData;

    public ISchedulingEngine WithPlayoutId(int playoutId)
    {
        _state.PlayoutId = playoutId;
        return this;
    }

    public ISchedulingEngine WithMode(PlayoutBuildMode mode)
    {
        _state.Mode = mode;
        return this;
    }

    public ISchedulingEngine WithSeed(int seed)
    {
        _state.Seed = seed;
        return this;
    }

    public ISchedulingEngine WithReferenceData(PlayoutReferenceData referenceData)
    {
        _referenceData = referenceData;
        return this;
    }

    public ISchedulingEngine BuildBetween(DateTimeOffset start, DateTimeOffset finish)
    {
        _state.Start = start;
        _state.Finish = finish;
        _state.CurrentTime = start;
        return this;
    }

    public ISchedulingEngine RemoveBefore(DateTimeOffset removeBefore)
    {
        _state.RemoveBefore = removeBefore;
        return this;
    }

    public ISchedulingEngine RestoreOrReset(Option<PlayoutAnchor> maybeAnchor)
    {
        if (_state.Mode is PlayoutBuildMode.Reset)
        {
            // erase items, not history
            _state.ClearItems = true;

            // remove any future or "currently active" history items
            // this prevents "walking" the playout forward by repeatedly resetting
            var toRemove = new List<PlayoutHistory>();
            toRemove.AddRange(
                _referenceData.PlayoutHistory.Filter(h =>
                    h.When > _state.Start.UtcDateTime ||
                    h.When <= _state.Start.UtcDateTime && h.Finish >= _state.Start.UtcDateTime));
            foreach (PlayoutHistory history in toRemove)
            {
                _state.HistoryToRemove.Add(history.Id);
            }
        }
        else
        {
            foreach (PlayoutAnchor anchor in maybeAnchor)
            {
                _state.CurrentTime = new DateTimeOffset(anchor.NextStart, TimeSpan.Zero).ToLocalTime();

                if (string.IsNullOrWhiteSpace(anchor.Context))
                {
                    break;
                }

                SerializedState state = JsonConvert.DeserializeObject<SerializedState>(anchor.Context);
                _state.LoadContext(state);
            }
        }

        return this;
    }

    public async Task AddCollection(
        string key,
        string collectionName,
        PlaybackOrder playbackOrder,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        List<MediaItem> items =
            await mediaCollectionRepository.GetCollectionItemsByName(collectionName, cancellationToken);
        if (items.Count == 0)
        {
            logger.LogWarning("Skipping invalid or empty collection {Name}", collectionName);
            return;
        }

        _enumeratorMediaItems[key] = items.ToImmutableList();
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        foreach (var enumerator in EnumeratorForContent(items, state, playbackOrder))
        {
            string historyKey = HistoryDetails.KeyForSchedulingContent(key, playbackOrder);
            var details = new EnumeratorDetails(enumerator, historyKey, playbackOrder);

            if (_enumerators.TryAdd(key, details))
            {
                logger.LogDebug(
                    "Added collection {Name} with key {Key} and order {Order}",
                    collectionName,
                    key,
                    playbackOrder);

                ApplyHistory(historyKey, items, enumerator, playbackOrder);
            }
        }
    }

    public async Task AddMarathon(
        string key,
        Dictionary<string, List<string>> guids,
        List<string> searches,
        string groupBy,
        bool shuffleGroups,
        PlaybackOrder itemPlaybackOrder,
        bool playAllItems)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        var helper = new MarathonHelper(mediaCollectionRepository);

        int index = _enumerators.Count;
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        Option<PlaylistContentResult> maybeResult = await helper.GetEnumerator(
            guids,
            searches,
            groupBy,
            shuffleGroups,
            itemPlaybackOrder,
            playAllItems,
            state,
            CancellationToken.None);

        foreach (PlaylistContentResult result in maybeResult)
        {
            foreach (PlaylistEnumerator enumerator in Optional(result.PlaylistEnumerator))
            {
                string historyKey = HistoryDetails.KeyForSchedulingMarathonContent(key, itemPlaybackOrder, groupBy);
                var details = new EnumeratorDetails(enumerator, historyKey, PlaybackOrder.None);

                if (_enumerators.TryAdd(key, details))
                {
                    logger.LogDebug("Added marathon with key {Key}", key);
                    ApplyPlaylistHistory(
                        historyKey,
                        result.Content,
                        enumerator);
                }
            }
        }
    }

    public async Task AddMultiCollection(
        string key,
        string multiCollectionName,
        PlaybackOrder playbackOrder,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        List<MediaItem> items =
            await mediaCollectionRepository.GetMultiCollectionItemsByName(multiCollectionName, cancellationToken);
        if (items.Count == 0)
        {
            logger.LogWarning("Skipping invalid or empty multi collection {Name}", multiCollectionName);
            return;
        }

        _enumeratorMediaItems[key] = items.ToImmutableList();
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        foreach (var enumerator in EnumeratorForContent(items, state, playbackOrder))
        {
            string historyKey = HistoryDetails.KeyForSchedulingContent(key, playbackOrder);
            var details = new EnumeratorDetails(enumerator, historyKey, playbackOrder);

            if (_enumerators.TryAdd(key, details))
            {
                logger.LogDebug(
                    "Added multi collection {Name} with key {Key} and order {Order}",
                    multiCollectionName,
                    key,
                    playbackOrder);

                ApplyHistory(historyKey, items, enumerator, playbackOrder);
            }
        }
    }

    public async Task AddPlaylist(
        string key,
        string playlist,
        string playlistGroup,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        Dictionary<PlaylistItem, List<MediaItem>> itemMap =
            await mediaCollectionRepository.GetPlaylistItemMap(playlistGroup, playlist, cancellationToken);

        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };

        var enumerator = await PlaylistEnumerator.Create(
            mediaCollectionRepository,
            itemMap,
            state,
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        string historyKey = HistoryDetails.KeyForSchedulingContent(key, PlaybackOrder.None);
        var details = new EnumeratorDetails(enumerator, historyKey, PlaybackOrder.None);

        if (_enumerators.TryAdd(key, details))
        {
            logger.LogDebug(
                "Added playlist {Group} / {Name} with key {Key}",
                playlistGroup,
                playlist,
                key);

            ApplyPlaylistHistory(
                historyKey,
                itemMap.ToImmutableDictionary(m => CollectionKey.ForPlaylistItem(m.Key), m => m.Value),
                enumerator);
        }
    }

    public async Task CreatePlaylist(
        string key,
        Dictionary<string, int> playlistItems,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        var helper = new PlaylistHelper(mediaCollectionRepository);

        int index = _enumerators.Count;
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        Option<PlaylistContentResult> maybeResult = await helper.GetEnumerator(
            _enumerators,
            _enumeratorMediaItems,
            playlistItems,
            state,
            CancellationToken.None);

        foreach (PlaylistContentResult result in maybeResult)
        {
            foreach (PlaylistEnumerator enumerator in Optional(result.PlaylistEnumerator))
            {
                string historyKey = HistoryDetails.KeyForSchedulingPlaylistContent(key);
                var details = new EnumeratorDetails(enumerator, historyKey, PlaybackOrder.None);

                if (_enumerators.TryAdd(key, details))
                {
                    logger.LogDebug("Created playlist with key {Key}", key);
                    ApplyPlaylistHistory(
                        historyKey,
                        result.Content,
                        enumerator);
                }
            }
        }
    }

    public async Task AddSmartCollection(
        string key,
        string smartCollectionName,
        PlaybackOrder playbackOrder,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        List<MediaItem> items =
            await mediaCollectionRepository.GetSmartCollectionItemsByName(smartCollectionName, cancellationToken);
        if (items.Count == 0)
        {
            logger.LogWarning("Skipping invalid or empty smart collection {Name}", smartCollectionName);
            return;
        }

        _enumeratorMediaItems[key] = items.ToImmutableList();
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        foreach (var enumerator in EnumeratorForContent(items, state, playbackOrder))
        {
            string historyKey = HistoryDetails.KeyForSchedulingContent(key, playbackOrder);
            var details = new EnumeratorDetails(enumerator, historyKey, playbackOrder);

            if (_enumerators.TryAdd(key, details))
            {
                logger.LogDebug(
                    "Added smart collection {Name} with key {Key} and order {Order}",
                    smartCollectionName,
                    key,
                    playbackOrder);

                ApplyHistory(historyKey, items, enumerator, playbackOrder);
            }
        }
    }

    public async Task AddSearch(
        string key,
        string query,
        PlaybackOrder playbackOrder,
        CancellationToken cancellationToken)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        List<MediaItem> items =
            await mediaCollectionRepository.GetSmartCollectionItems(query, string.Empty, cancellationToken);
        if (items.Count == 0)
        {
            logger.LogWarning("Skipping invalid or empty search query {Query}", query);
            return;
        }

        _enumeratorMediaItems[key] = items.ToImmutableList();
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        foreach (var enumerator in EnumeratorForContent(items, state, playbackOrder))
        {
            string historyKey = HistoryDetails.KeyForSchedulingContent(key, playbackOrder);
            var details = new EnumeratorDetails(enumerator, historyKey, playbackOrder);

            if (_enumerators.TryAdd(key, details))
            {
                logger.LogDebug(
                    "Added search query {Query} with key {Key} and order {Order}",
                    query,
                    key,
                    playbackOrder);

                ApplyHistory(historyKey, items, enumerator, playbackOrder);
            }
        }
    }

    public async Task AddShow(
        string key,
        Dictionary<string, string> guids,
        PlaybackOrder playbackOrder)
    {
        if (_enumerators.ContainsKey(key))
        {
            return;
        }

        int index = _enumerators.Count;
        List<MediaItem> items =
            await mediaCollectionRepository.GetShowItemsByShowGuids(
                guids.Map(g => $"{g.Key}://{g.Value}").ToList());
        if (items.Count == 0)
        {
            logger.LogWarning("Skipping invalid or empty show with key {Key}", key);
            return;
        }

        _enumeratorMediaItems[key] = items.ToImmutableList();
        var state = new CollectionEnumeratorState { Seed = _state.Seed + index, Index = 0 };
        foreach (var enumerator in EnumeratorForContent(items, state, playbackOrder))
        {
            string historyKey = HistoryDetails.KeyForSchedulingContent(key, playbackOrder);
            var details = new EnumeratorDetails(enumerator, historyKey, playbackOrder);

            if (_enumerators.TryAdd(key, details))
            {
                logger.LogDebug(
                    "Added show with key {Key} and order {Order}",
                    key,
                    playbackOrder);

                ApplyHistory(historyKey, items, enumerator, playbackOrder);
            }
        }
    }

    public bool AddAll(string content, Option<FillerKind> fillerKind, string customTitle, bool disableWatermarks)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        int enumeratorCount = enumeratorDetails.Enumerator is PlaylistEnumerator playlistEnumerator
            ? playlistEnumerator.CountForFiller
            : enumeratorDetails.Enumerator.Count;

        return AddCountInternal(
            enumeratorDetails,
            enumeratorCount,
            fillerKind,
            customTitle,
            disableWatermarks);
    }

    public bool AddCount(
        string content,
        int count,
        Option<FillerKind> fillerKind,
        string customTitle,
        bool disableWatermarks)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        return AddCountInternal(enumeratorDetails, count, fillerKind, customTitle, disableWatermarks);
    }

    public bool AddDuration(
        string content,
        string duration,
        string fallback,
        bool trim,
        int discardAttempts,
        bool stopBeforeEnd,
        bool offlineTail,
        Option<FillerKind> maybeFillerKind,
        string customTitle,
        bool disableWatermarks)
    {
        if (!TimeSpanParser.TryParse(duration, out TimeSpan timeSpan))
        {
            logger.LogWarning("Skipping invalid duration {Duration} for content {Key}", duration, content);
            return false;
        }

        if (!stopBeforeEnd && offlineTail)
        {
            logger.LogError("offline_tail must be false when stop_before_end is false");
            return false;
        }

        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        EnumeratorDetails fallbackEnumeratorDetails = null;
        if (!string.IsNullOrEmpty(fallback))
        {
            _enumerators.TryGetValue(fallback, out fallbackEnumeratorDetails);
        }

        DateTimeOffset targetTime = _state.CurrentTime.Add(timeSpan);

        _state.CurrentTime = AddDurationInternal(
            targetTime,
            stopBeforeEnd,
            discardAttempts,
            trim,
            offlineTail,
            GetFillerKind(maybeFillerKind),
            customTitle,
            disableWatermarks,
            enumeratorDetails,
            Optional(fallbackEnumeratorDetails));

        return true;
    }

    public bool PadToNext(
        string content,
        int minutes,
        string fallback,
        bool trim,
        int discardAttempts,
        bool stopBeforeEnd,
        bool offlineTail,
        Option<FillerKind> maybeFillerKind,
        string customTitle,
        bool disableWatermarks)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        EnumeratorDetails fallbackEnumeratorDetails = null;
        if (!string.IsNullOrEmpty(fallback))
        {
            _enumerators.TryGetValue(fallback, out fallbackEnumeratorDetails);
        }

        int currentMinute = _state.CurrentTime.Minute;

        int targetMinute = (currentMinute + minutes - 1) / minutes * minutes;

        DateTimeOffset almostTargetTime =
            _state.CurrentTime - TimeSpan.FromMinutes(currentMinute) + TimeSpan.FromMinutes(targetMinute);

        var targetTime = new DateTimeOffset(
            almostTargetTime.Year,
            almostTargetTime.Month,
            almostTargetTime.Day,
            almostTargetTime.Hour,
            almostTargetTime.Minute,
            0,
            almostTargetTime.Offset);

        // ensure filler works for content less than one minute
        if (targetTime <= _state.CurrentTime)
        {
            targetTime = targetTime.AddMinutes(minutes);
        }

        _state.CurrentTime = AddDurationInternal(
            targetTime,
            stopBeforeEnd,
            discardAttempts,
            trim,
            offlineTail,
            GetFillerKind(maybeFillerKind),
            customTitle,
            disableWatermarks,
            enumeratorDetails,
            Optional(fallbackEnumeratorDetails));

        return true;
    }

    public bool PadUntil(
        string content,
        string padUntil,
        bool tomorrow,
        string fallback,
        bool trim,
        int discardAttempts,
        bool stopBeforeEnd,
        bool offlineTail,
        Option<FillerKind> maybeFillerKind,
        string customTitle,
        bool disableWatermarks)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        EnumeratorDetails fallbackEnumeratorDetails = null;
        if (!string.IsNullOrEmpty(fallback))
        {
            _enumerators.TryGetValue(fallback, out fallbackEnumeratorDetails);
        }

        if (!TimeOnly.TryParse(padUntil, out TimeOnly padUntilTime))
        {
            logger.LogWarning("Skipping pad_until with invalid 'when' {When}", padUntil);
            return false;
        }

        DateTimeOffset targetTime = _state.CurrentTime;

        var dayOnly = DateOnly.FromDateTime(targetTime.LocalDateTime);
        var timeOnly = TimeOnly.FromDateTime(targetTime.LocalDateTime);

        if (timeOnly > padUntilTime)
        {
            if (tomorrow)
            {
                // this is wrong when offset changes
                dayOnly = dayOnly.AddDays(1);
                targetTime = new DateTimeOffset(dayOnly, padUntilTime, targetTime.Offset);
            }
        }
        else
        {
            // this is wrong when offset changes
            targetTime = new DateTimeOffset(dayOnly, padUntilTime, targetTime.Offset);
        }

        _state.CurrentTime = AddDurationInternal(
            targetTime,
            stopBeforeEnd,
            discardAttempts,
            trim,
            offlineTail,
            GetFillerKind(maybeFillerKind),
            customTitle,
            disableWatermarks,
            enumeratorDetails,
            Optional(fallbackEnumeratorDetails));

        return true;
    }

    public bool PadUntilExact(
        string content,
        DateTimeOffset padUntil,
        string fallback,
        bool trim,
        int discardAttempts,
        bool stopBeforeEnd,
        bool offlineTail,
        Option<FillerKind> maybeFillerKind,
        string customTitle,
        bool disableWatermarks)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid content {Key}", content);
            return false;
        }

        EnumeratorDetails fallbackEnumeratorDetails = null;
        if (!string.IsNullOrEmpty(fallback))
        {
            _enumerators.TryGetValue(fallback, out fallbackEnumeratorDetails);
        }

        DateTimeOffset targetTime = _state.CurrentTime;
        if (targetTime < padUntil)
        {
            // this is wrong when offset changes?
            targetTime = padUntil.ToLocalTime();
        }

        _state.CurrentTime = AddDurationInternal(
            targetTime,
            stopBeforeEnd,
            discardAttempts,
            trim,
            offlineTail,
            GetFillerKind(maybeFillerKind),
            customTitle,
            disableWatermarks,
            enumeratorDetails,
            Optional(fallbackEnumeratorDetails));

        return true;
    }

    private DateTimeOffset AddDurationInternal(
        DateTimeOffset targetTime,
        bool stopBeforeEnd,
        int discardAttempts,
        bool trim,
        bool offlineTail,
        FillerKind fillerKind,
        string customTitle,
        bool disableWatermarks,
        EnumeratorDetails enumeratorDetails,
        Option<EnumeratorDetails> maybeFallbackEnumeratorDetails)
    {
        var done = false;
        TimeSpan remainingToFill = targetTime - _state.CurrentTime;
        while (!done && enumeratorDetails.Enumerator.Current.IsSome && remainingToFill > TimeSpan.Zero)
        {
            foreach (string preRollPlaylist in _state.GetPreRollPlaylist())
            {
                AddFillerPlaylist(preRollPlaylist, FillerKind.PreRoll);
                remainingToFill = targetTime - _state.CurrentTime;
                if (remainingToFill <= TimeSpan.Zero)
                {
                    // TODO: this shouldn't be needed, but prevents overlap
                    _state.AddedItems.RemoveAll(pi => pi.FinishOffset >= targetTime);
                    _state.CurrentTime = _state.AddedItems.Max(pi => pi.FinishOffset);
                    break;
                }
            }

            foreach (MediaItem mediaItem in enumeratorDetails.Enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                var playoutItem = new PlayoutItem
                {
                    PlayoutId = _state.PlayoutId,
                    MediaItemId = mediaItem.Id,
                    Start = _state.CurrentTime.UtcDateTime,
                    Finish = _state.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    GuideGroup = _state.PeekNextGuideGroup(),
                    FillerKind = fillerKind,
                    CustomTitle = null,
                    DisableWatermarks = disableWatermarks,
                    PlayoutItemWatermarks = [],
                    PlayoutItemGraphicsElements = []
                };

                if (!string.IsNullOrWhiteSpace(customTitle))
                {
                    playoutItem.CustomTitle = customTitle;
                }
                else if (!string.IsNullOrWhiteSpace(_state.CustomTitle))
                {
                    playoutItem.CustomTitle = _state.CustomTitle;
                }

                foreach (int watermarkId in _state.GetChannelWatermarkIds())
                {
                    playoutItem.PlayoutItemWatermarks.Add(
                        new PlayoutItemWatermark
                        {
                            PlayoutItem = playoutItem,
                            WatermarkId = watermarkId
                        });
                }

                foreach ((int graphicsElementId, string variablesJson) in _state.GetGraphicsElements())
                {
                    playoutItem.PlayoutItemGraphicsElements.Add(
                        new PlayoutItemGraphicsElement
                        {
                            PlayoutItem = playoutItem,
                            GraphicsElementId = graphicsElementId,
                            Variables = variablesJson
                        });
                }

                if (remainingToFill - itemDuration >= TimeSpan.Zero || !stopBeforeEnd)
                {
                    _state.AddedItems.Add(playoutItem);
                    _state.AdvanceGuideGroup();

                    // create history record
                    List<PlayoutHistory> maybeHistory = GetHistoryForItem(enumeratorDetails, playoutItem, mediaItem);
                    foreach (PlayoutHistory history in maybeHistory)
                    {
                        _state.AddedHistory.Add(history);
                    }

                    remainingToFill -= itemDuration;
                    _state.CurrentTime += itemDuration;

                    enumeratorDetails.Enumerator.MoveNext(playoutItem.StartOffset);
                }
                else if (discardAttempts > 0)
                {
                    // item won't fit; try the next one
                    discardAttempts--;
                    enumeratorDetails.Enumerator.MoveNext(Option<DateTimeOffset>.None);
                }
                else if (trim)
                {
                    // trim item to exactly fit
                    playoutItem.Finish = targetTime.UtcDateTime;
                    playoutItem.OutPoint = playoutItem.Finish - playoutItem.Start;

                    _state.AddedItems.Add(playoutItem);
                    _state.AdvanceGuideGroup();

                    // create history record
                    List<PlayoutHistory> maybeHistory = GetHistoryForItem(enumeratorDetails, playoutItem, mediaItem);
                    foreach (PlayoutHistory history in maybeHistory)
                    {
                        _state.AddedHistory.Add(history);
                    }

                    remainingToFill = TimeSpan.Zero;
                    _state.CurrentTime = targetTime;

                    enumeratorDetails.Enumerator.MoveNext(playoutItem.StartOffset);
                }
                else if (maybeFallbackEnumeratorDetails.IsSome)
                {
                    foreach (EnumeratorDetails fallbackEnumeratorDetails in maybeFallbackEnumeratorDetails)
                    {
                        remainingToFill = TimeSpan.Zero;
                        _state.CurrentTime = targetTime;
                        done = true;

                        // replace with fallback content
                        foreach (MediaItem fallbackItem in fallbackEnumeratorDetails.Enumerator.Current)
                        {
                            playoutItem.MediaItemId = fallbackItem.Id;
                            playoutItem.Finish = targetTime.UtcDateTime;
                            playoutItem.FillerKind = FillerKind.Fallback;

                            _state.AddedItems.Add(playoutItem);

                            // create history record
                            List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                                fallbackEnumeratorDetails,
                                playoutItem,
                                mediaItem);

                            foreach (PlayoutHistory history in maybeHistory)
                            {
                                _state.AddedHistory.Add(history);
                            }

                            fallbackEnumeratorDetails.Enumerator.MoveNext(playoutItem.StartOffset);
                        }
                    }
                }
                else
                {
                    // item won't fit; we're done
                    done = true;
                }
            }

            // foreach (string postRollSequence in context.GetPostRollSequence())
            // {
            //     context.PushFillerKind(FillerKind.PostRoll);
            //     await executeSequence(postRollSequence);
            //     context.PopFillerKind();
            // }
        }

        if (!stopBeforeEnd)
        {
            return _state.CurrentTime;
        }

        return offlineTail ? targetTime : _state.CurrentTime;
    }

    private void AddFillerPlaylist(string playlist, FillerKind fillerKind)
    {
        if (!_enumerators.TryGetValue(playlist, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Skipping invalid filler playlist {Key}", playlist);
            return;
        }

        if (enumeratorDetails.Enumerator is PlaylistEnumerator playlistEnumerator)
        {
            int count = playlistEnumerator.CountForFiller;
            AddCountInternal(
                enumeratorDetails,
                count,
                fillerKind,
                customTitle: null,
                disableWatermarks: true,
                disableFiller: true);
        }
    }

    private bool AddCountInternal(
        EnumeratorDetails enumeratorDetails,
        int count,
        Option<FillerKind> fillerKind,
        string customTitle,
        bool disableWatermarks,
        bool disableFiller = false)
    {
        var result = false;

        for (var i = 0; i < count; i++)
        {
            if (!disableFiller)
            {
                foreach (string preRollPlaylist in _state.GetPreRollPlaylist())
                {
                    AddFillerPlaylist(preRollPlaylist, FillerKind.PreRoll);
                }
            }

            foreach (MediaItem mediaItem in enumeratorDetails.Enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                // create a playout item
                var playoutItem = new PlayoutItem
                {
                    PlayoutId = _state.PlayoutId,
                    MediaItemId = mediaItem.Id,
                    Start = _state.CurrentTime.UtcDateTime,
                    Finish = _state.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    FillerKind = GetFillerKind(fillerKind),
                    CustomTitle = null,
                    DisableWatermarks = disableWatermarks,
                    GuideGroup = _state.PeekNextGuideGroup(),
                    PlayoutItemWatermarks = [],
                    PlayoutItemGraphicsElements = []
                };

                if (!string.IsNullOrWhiteSpace(customTitle))
                {
                    playoutItem.CustomTitle = customTitle;
                }
                else if (!string.IsNullOrWhiteSpace(_state.CustomTitle))
                {
                    playoutItem.CustomTitle = _state.CustomTitle;
                }

                foreach (int watermarkId in _state.GetChannelWatermarkIds())
                {
                    playoutItem.PlayoutItemWatermarks.Add(
                        new PlayoutItemWatermark
                        {
                            PlayoutItem = playoutItem,
                            WatermarkId = watermarkId
                        });
                }

                foreach ((int graphicsElementId, string variablesJson) in _state.GetGraphicsElements())
                {
                    playoutItem.PlayoutItemGraphicsElements.Add(
                        new PlayoutItemGraphicsElement
                        {
                            PlayoutItem = playoutItem,
                            GraphicsElementId = graphicsElementId,
                            Variables = variablesJson
                        });
                }

                //await AddItemAndMidRoll(context, playoutItem, mediaItem, executeSequence);

                _state.AddedItems.Add(playoutItem);
                _state.CurrentTime += playoutItem.OutPoint - playoutItem.InPoint;
                _state.AdvanceGuideGroup();

                // create history record
                List<PlayoutHistory> maybeHistory = GetHistoryForItem(enumeratorDetails, playoutItem, mediaItem);
                foreach (PlayoutHistory history in maybeHistory)
                {
                    _state.AddedHistory.Add(history);
                }

                enumeratorDetails.Enumerator.MoveNext(playoutItem.StartOffset);

                result = true;
            }

            // foreach (string postRollSequence in context.GetPostRollSequence())
            // {
            //     context.PushFillerKind(FillerKind.PostRoll);
            //     await executeSequence(postRollSequence);
            //     context.PopFillerKind();
            // }
        }

        return result;
    }

    public Option<MediaItem> PeekNext(string content)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Unable to peek next item for invalid content {Key}", content);
            return Option<MediaItem>.None;
        }

        return enumeratorDetails.Enumerator.Current;
    }

    public void LockGuideGroup(bool advance, string customTitle)
    {
        _state.LockGuideGroup(advance, customTitle);
    }

    public void UnlockGuideGroup()
    {
        _state.UnlockGuideGroup();
    }

    public async Task GraphicsOn(
        List<string> graphicsElements,
        Dictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        string variablesJson = null;
        if (variables.Count > 0)
        {
            variablesJson = JsonConvert.SerializeObject(variables, JsonSettings);
        }

        foreach (string element in graphicsElements.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            foreach (GraphicsElement ge in await GetGraphicsElementByPath(element, cancellationToken))
            {
                _state.SetGraphicsElement(ge.Id, variablesJson);
            }
        }
    }

    public async Task GraphicsOff(List<string> graphicsElements, CancellationToken cancellationToken)
    {
        if (graphicsElements.Count == 0)
        {
            _state.ClearGraphicsElements();
        }
        else
        {
            foreach (string element in graphicsElements.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                foreach (GraphicsElement ge in await GetGraphicsElementByPath(element, cancellationToken))
                {
                    _state.RemoveGraphicsElement(ge.Id);
                }
            }
        }
    }

    public async Task WatermarkOn(List<string> watermarks)
    {
        foreach (string watermark in watermarks.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            foreach (ChannelWatermark wm in await GetChannelWatermarkByName(watermark))
            {
                _state.SetChannelWatermarkId(wm.Id);
            }
        }
    }

    public async Task WatermarkOff(List<string> watermarks)
    {
        if (watermarks.Count == 0)
        {
            _state.ClearChannelWatermarkIds();
        }
        else
        {
            foreach (string watermark in watermarks.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                foreach (ChannelWatermark wm in await GetChannelWatermarkByName(watermark))
                {
                    _state.RemoveChannelWatermarkId(wm.Id);
                }
            }
        }
    }

    public void PreRollOn(string content) => _state.PreRollOn(content);

    public void PreRollOff() => _state.PreRollOff();

    public void SkipItems(string content, int count)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Unable to skip items for invalid content {Key}", content);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            enumeratorDetails.Enumerator.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    public void SkipToItem(string content, int season, int episode)
    {
        if (!_enumerators.TryGetValue(content, out EnumeratorDetails enumeratorDetails))
        {
            logger.LogWarning("Unable to skip items for invalid content {Key}", content);
            return;
        }

        if (season < 0 || episode < 1)
        {
            logger.LogWarning("Unable to skip to invalid season/episode: {Season}/{Episode}", season, episode);
            return;
        }

        var done = false;
        for (var index = 0; index < enumeratorDetails.Enumerator.Count; index++)
        {
            if (done)
            {
                break;
            }

            foreach (MediaItem mediaItem in enumeratorDetails.Enumerator.Current)
            {
                if (mediaItem is Episode e)
                {
                    if (e.Season?.SeasonNumber == season &&
                        e.EpisodeMetadata.HeadOrNone().Map(em => em.EpisodeNumber) == episode)
                    {
                        done = true;
                        break;
                    }
                }

                enumeratorDetails.Enumerator.MoveNext(Option<DateTimeOffset>.None);
            }
        }
    }

    public ISchedulingEngine WaitUntil(TimeOnly waitUntil, bool tomorrow, bool rewindOnReset)
    {
        var currentTime = _state.CurrentTime;

        var dayOnly = DateOnly.FromDateTime(currentTime.LocalDateTime);
        var timeOnly = TimeOnly.FromDateTime(currentTime.LocalDateTime);

        if (timeOnly > waitUntil)
        {
            if (tomorrow)
            {
                // this is wrong when offset changes
                dayOnly = dayOnly.AddDays(1);
                currentTime = new DateTimeOffset(dayOnly, waitUntil, currentTime.Offset);
            }
            else if (rewindOnReset && _state.Mode == PlayoutBuildMode.Reset)
            {
                // maybe wrong when offset changes?
                currentTime = new DateTimeOffset(dayOnly, waitUntil, currentTime.Offset);
            }
        }
        else
        {
            // this is wrong when offset changes
            currentTime = new DateTimeOffset(dayOnly, waitUntil, currentTime.Offset);
        }

        _state.CurrentTime = currentTime;

        return this;
    }

    public ISchedulingEngine WaitUntilExact(DateTimeOffset waitUntil, bool rewindOnReset)
    {
        var currentTime = _state.CurrentTime;

        if (currentTime > waitUntil)
        {
            if (rewindOnReset && _state.Mode == PlayoutBuildMode.Reset)
            {
                // maybe wrong when offset changes?
                currentTime = waitUntil.ToLocalTime();
            }
        }
        else
        {
            // this is wrong when offset changes?
            currentTime = waitUntil.ToLocalTime();
        }

        _state.CurrentTime = currentTime;

        return this;
    }

    public PlayoutAnchor GetAnchor()
    {
        DateTime maxTime = _state.CurrentTime.UtcDateTime;
        if (_state.AddedItems.Count > 0)
        {
            maxTime = _state.AddedItems.Max(i => i.Finish);
        }

        return new PlayoutAnchor
        {
            NextStart = maxTime,
            Context = _state.SerializeContext()
        };
    }

    public ISchedulingEngineState GetState()
    {
        return _state;
    }

    private static Option<IMediaCollectionEnumerator> EnumeratorForContent(
        List<MediaItem> items,
        CollectionEnumeratorState state,
        PlaybackOrder playbackOrder,
        bool multiPart = false)
    {
        switch (playbackOrder)
        {
            case PlaybackOrder.Chronological:
                return new ChronologicalMediaCollectionEnumerator(items, state);
            case PlaybackOrder.Shuffle:
                bool keepMultiPartEpisodesTogether = multiPart;
                List<GroupedMediaItem> groupedMediaItems = keepMultiPartEpisodesTogether
                    ? MultiPartEpisodeGrouper.GroupMediaItems(items, false)
                    : items.Map(mi => new GroupedMediaItem(mi, null)).ToList();
                return new BlockPlayoutShuffledMediaCollectionEnumerator(groupedMediaItems, state);
        }

        return Option<IMediaCollectionEnumerator>.None;
    }

    private void ApplyPlaylistHistory(
        string historyKey,
        ImmutableDictionary<CollectionKey, List<MediaItem>> itemMap,
        PlaylistEnumerator playlistEnumerator)
    {
        DateTime historyTime = _state.CurrentTime.UtcDateTime;

        var filteredHistory = _referenceData.PlayoutHistory.ToList();
        filteredHistory.RemoveAll(h => _state.HistoryToRemove.Contains(h.Id));

        Option<DateTime> maxWhen = filteredHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .Map(h => h.When)
            .OrderByDescending(h => h)
            .HeadOrNone()
            .IfNone(DateTime.MinValue);

        var maybeHistory = filteredHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When == maxWhen)
            .ToList();

        Option<PlayoutHistory> maybePrimaryHistory = maybeHistory
            .Filter(h => string.IsNullOrWhiteSpace(h.ChildKey))
            .HeadOrNone();

        foreach (PlayoutHistory primaryHistory in maybePrimaryHistory)
        {
            var hasSetEnumeratorIndex = false;

            var childEnumeratorKeys = playlistEnumerator.ChildEnumerators.Map(x => x.CollectionKey).ToList();
            foreach ((IMediaCollectionEnumerator childEnumerator, CollectionKey collectionKey) in
                     playlistEnumerator.ChildEnumerators)
            {
                PlaybackOrder itemPlaybackOrder = childEnumerator switch
                {
                    ChronologicalMediaCollectionEnumerator => PlaybackOrder.Chronological,
                    RandomizedMediaCollectionEnumerator => PlaybackOrder.Random,
                    ShuffledMediaCollectionEnumerator => PlaybackOrder.Shuffle,
                    _ => PlaybackOrder.None
                };

                Option<PlayoutHistory> maybeApplicableHistory = maybeHistory
                    .Filter(h => h.ChildKey == HistoryDetails.KeyForCollectionKey(collectionKey))
                    .HeadOrNone();

                if (!itemMap.TryGetValue(collectionKey, out List<MediaItem> collectionItems) ||
                    collectionItems.Count == 0)
                {
                    continue;
                }

                foreach (PlayoutHistory h in maybeApplicableHistory)
                {
                    // logger.LogDebug(
                    //     "History is applicable: {When}: {ChildKey} / {History} / {IsCurrentChild}",
                    //     h.When,
                    //     h.ChildKey,
                    //     h.Details,
                    //     h.IsCurrentChild);

                    playlistEnumerator.ResetState(
                        new CollectionEnumeratorState
                        {
                            Seed = playlistEnumerator.State.Seed,
                            Index = h.Index + (h.IsCurrentChild ? 1 : 0)
                        });

                    if (itemPlaybackOrder is PlaybackOrder.Chronological)
                    {
                        HistoryDetails.MoveToNextItem(
                            collectionItems,
                            h.Details,
                            childEnumerator,
                            itemPlaybackOrder,
                            true);
                    }

                    if (h.IsCurrentChild)
                    {
                        // try to find enumerator based on collection key
                        playlistEnumerator.SetEnumeratorIndex(childEnumeratorKeys.IndexOf(collectionKey));
                        hasSetEnumeratorIndex = true;
                    }
                }
            }

            if (!hasSetEnumeratorIndex)
            {
                // falling back to enumerator based on index
                playlistEnumerator.SetEnumeratorIndex(primaryHistory.Index);
            }

            // only move next at the end, because that may also move
            // the enumerator index
            playlistEnumerator.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    private void ApplyHistory(
        string historyKey,
        List<MediaItem> collectionItems,
        IMediaCollectionEnumerator enumerator,
        PlaybackOrder playbackOrder)
    {
        DateTime historyTime = _state.CurrentTime.UtcDateTime;

        var filteredHistory = _referenceData.PlayoutHistory.ToList();
        filteredHistory.RemoveAll(h => _state.HistoryToRemove.Contains(h.Id));

        Option<DateTime> maxWhen = filteredHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .Map(h => h.When)
            .OrderByDescending(h => h)
            .HeadOrNone()
            .IfNone(DateTime.MinValue);

        var maybeHistory = filteredHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When == maxWhen)
            .ToList();

        if (enumerator is PlaylistEnumerator)
        {
            return;
        }

        if (collectionItems.Count == 0)
        {
            return;
        }

        // seek to the appropriate place in the collection enumerator
        foreach (PlayoutHistory h in maybeHistory)
        {
            // logger.LogDebug("History is applicable: {When}: {History}", h.When, h.Details);

            enumerator.ResetState(new CollectionEnumeratorState { Seed = enumerator.State.Seed, Index = h.Index + 1 });

            if (playbackOrder is PlaybackOrder.Chronological)
            {
                HistoryDetails.MoveToNextItem(
                    collectionItems,
                    h.Details,
                    enumerator,
                    playbackOrder);
            }
        }
    }

    private List<PlayoutHistory> GetHistoryForItem(
        EnumeratorDetails enumeratorDetails,
        PlayoutItem playoutItem,
        MediaItem mediaItem)
    {
        var result = new List<PlayoutHistory>();

        if (enumeratorDetails.Enumerator is PlaylistEnumerator playlistEnumerator)
        {
            // create a playout history record
            var nextHistory = new PlayoutHistory
            {
                PlayoutId = _state.PlayoutId,
                PlaybackOrder = enumeratorDetails.PlaybackOrder,
                Index = playlistEnumerator.EnumeratorIndex,
                When = playoutItem.StartOffset.UtcDateTime,
                Finish = playoutItem.FinishOffset.UtcDateTime,
                Key = enumeratorDetails.HistoryKey,
                Details = HistoryDetails.ForMediaItem(mediaItem)
            };

            result.Add(nextHistory);

            for (var i = 0; i < playlistEnumerator.ChildEnumerators.Count; i++)
            {
                (IMediaCollectionEnumerator childEnumerator, CollectionKey collectionKey) =
                    playlistEnumerator.ChildEnumerators[i];
                bool isCurrentChild = i == playlistEnumerator.EnumeratorIndex;
                foreach (MediaItem currentMediaItem in childEnumerator.Current)
                {
                    // create a playout history record
                    var childHistory = new PlayoutHistory
                    {
                        PlayoutId = _state.PlayoutId,
                        PlaybackOrder = enumeratorDetails.PlaybackOrder,
                        Index = childEnumerator.State.Index,
                        When = playoutItem.StartOffset.UtcDateTime,
                        Finish = playoutItem.FinishOffset.UtcDateTime,
                        Key = enumeratorDetails.HistoryKey,
                        ChildKey = HistoryDetails.KeyForCollectionKey(collectionKey),
                        IsCurrentChild = isCurrentChild,
                        Details = HistoryDetails.ForMediaItem(currentMediaItem)
                    };

                    result.Add(childHistory);
                }
            }
        }
        else
        {
            // create a playout history record
            var nextHistory = new PlayoutHistory
            {
                PlayoutId = _state.PlayoutId,
                PlaybackOrder = enumeratorDetails.PlaybackOrder,
                Index = enumeratorDetails.Enumerator.State.Index,
                When = playoutItem.StartOffset.UtcDateTime,
                Finish = playoutItem.FinishOffset.UtcDateTime,
                Key = enumeratorDetails.HistoryKey,
                Details = HistoryDetails.ForMediaItem(mediaItem)
            };

            result.Add(nextHistory);
        }

        return result;
    }

    protected static FillerKind GetFillerKind(Option<FillerKind> maybeFillerKind)
    {
        foreach (FillerKind fillerKind in maybeFillerKind)
        {
            return fillerKind;
        }

        return FillerKind.None;
    }

    private async Task<Option<GraphicsElement>> GetGraphicsElementByPath(
        string path,
        CancellationToken cancellationToken)
    {
        if (_graphicsElementCache.TryGetValue(path, out Option<GraphicsElement> cachedGraphicsElement))
        {
            foreach (GraphicsElement graphicsElement in cachedGraphicsElement)
            {
                return graphicsElement;
            }
        }
        else
        {
            Option<GraphicsElement> maybeGraphicsElement =
                await graphicsElementRepository.GetGraphicsElementByPath(path, cancellationToken);
            _graphicsElementCache.Add(path, maybeGraphicsElement);
            foreach (GraphicsElement graphicsElement in maybeGraphicsElement)
            {
                return graphicsElement;
            }
        }

        return Option<GraphicsElement>.None;
    }

    private async Task<Option<ChannelWatermark>> GetChannelWatermarkByName(string name)
    {
        if (_watermarkCache.TryGetValue(name, out Option<ChannelWatermark> cachedWatermark))
        {
            foreach (ChannelWatermark channelWatermark in cachedWatermark)
            {
                return channelWatermark;
            }
        }
        else
        {
            Option<ChannelWatermark> maybeWatermark = await channelRepository.GetWatermarkByName(name);
            _watermarkCache.Add(name, maybeWatermark);
            foreach (ChannelWatermark channelWatermark in maybeWatermark)
            {
                return channelWatermark;
            }
        }

        return Option<ChannelWatermark>.None;
    }

    public record SerializedState(
        int? GuideGroup,
        bool? GuideGroupLocked,
        string PreRollPlaylist);

    private class SchedulingEngineState(int guideGroup) : ISchedulingEngineState
    {
        private int _guideGroup = guideGroup;
        private bool _guideGroupLocked;
        private bool _guideGroupTitle;
        private readonly Dictionary<int, string> _graphicsElements = [];
        private readonly System.Collections.Generic.HashSet<int> _channelWatermarkIds = [];
        private readonly Stack<FillerKind> _fillerKind = new();
        private Option<string> _preRollPlaylist = Option<string>.None;

        // track is_done calls when current_time has not advanced
        private DateTimeOffset _lastCheckedTime;
        private int _noProgressCounter;
        private const int MaxCallsNoProgress = 20;

        // state
        public int PlayoutId { get; set; }
        public PlayoutBuildMode Mode { get; set; }
        public int Seed { get; set; }
        public DateTimeOffset Finish { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset CurrentTime { get; set; }
        public string CustomTitle { get; private set; }

        // guide group
        public int PeekNextGuideGroup()
        {
            if (_guideGroupLocked)
            {
                return _guideGroup;
            }

            int result = _guideGroup + 1;
            if (result > 1000)
            {
                result = 1;
            }

            return result;
        }

        public void AdvanceGuideGroup()
        {
            if (_guideGroupLocked)
            {
                return;
            }

            _guideGroup++;
            if (_guideGroup > 1000)
            {
                _guideGroup = 1;
            }
        }

        public void LockGuideGroup(bool advance = true, string customTitle = null)
        {
            if (advance)
            {
                AdvanceGuideGroup();
            }

            _guideGroupLocked = true;

            if (!string.IsNullOrWhiteSpace(customTitle))
            {
                _guideGroupTitle = true;
                CustomTitle = customTitle;
            }
        }

        public void UnlockGuideGroup()
        {
            _guideGroupLocked = false;

            if (_guideGroupTitle)
            {
                CustomTitle = null;
            }

            _guideGroupTitle = false;
        }

        public void SetGraphicsElement(int id, string variablesJson) => _graphicsElements.Add(id, variablesJson);
        public void RemoveGraphicsElement(int id) => _graphicsElements.Remove(id);
        public void ClearGraphicsElements() => _graphicsElements.Clear();
        public Dictionary<int, string> GetGraphicsElements() => _graphicsElements;

        public void SetChannelWatermarkId(int id) => _channelWatermarkIds.Add(id);
        public void RemoveChannelWatermarkId(int id) => _channelWatermarkIds.Remove(id);
        public void ClearChannelWatermarkIds() => _channelWatermarkIds.Clear();
        public List<int> GetChannelWatermarkIds() => _channelWatermarkIds.ToList();

        public void PreRollOn(string playlist) => _preRollPlaylist = playlist;
        public void PreRollOff() => _preRollPlaylist = Option<string>.None;
        public Option<string> GetPreRollPlaylist() => _preRollPlaylist;

        // result
        public Option<DateTimeOffset> RemoveBefore { get; set; }
        public bool ClearItems { get; set; }
        public List<PlayoutItem> AddedItems { get; } = [];
        public System.Collections.Generic.HashSet<int> HistoryToRemove { get; } = [];
        public List<PlayoutHistory> AddedHistory { get; } = [];

        public bool IsDone
        {
            get
            {
                if (CurrentTime == _lastCheckedTime)
                {
                    _noProgressCounter++;
                    if (_noProgressCounter >= MaxCallsNoProgress)
                    {
                        throw new InvalidOperationException(
                            $"Script execution halted after {MaxCallsNoProgress} consecutive calls to is_done() without time advancing.");
                    }
                }
                else
                {
                    _lastCheckedTime = CurrentTime;
                    _noProgressCounter = 0;
                }

                return CurrentTime >= Finish;
            }
        }

        public string SerializeContext()
        {
            string preRollPlaylist = null;
            foreach (string playlist in _preRollPlaylist)
            {
                preRollPlaylist = playlist;
            }

            var state = new SerializedState(
                _guideGroup,
                _guideGroupLocked,
                preRollPlaylist);

            return JsonConvert.SerializeObject(state, Formatting.None, JsonSettings);
        }

        public void LoadContext(SerializedState state)
        {
            foreach (int guideGroup in Optional(state.GuideGroup))
            {
                _guideGroup = guideGroup;
            }

            foreach (bool guideGroupLocked in Optional(state.GuideGroupLocked))
            {
                _guideGroupLocked = guideGroupLocked;
            }
        }
    }
}
