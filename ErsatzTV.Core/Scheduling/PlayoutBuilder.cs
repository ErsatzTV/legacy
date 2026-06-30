using System.IO.Abstractions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Map = LanguageExt.Map;

namespace ErsatzTV.Core.Scheduling;

// TODO: these tests fail on days when offset changes
// because the change happens during the playout
public class PlayoutBuilder : IPlayoutBuilder
{
    private readonly IArtistRepository _artistRepository;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IRerunHelper _rerunHelper;
    private readonly IMediaCollectionRepository _mediaCollectionRepository;
    private readonly IMultiEpisodeShuffleCollectionEnumeratorFactory _multiEpisodeFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ITelevisionRepository _televisionRepository;
    private Playlist _debugPlaylist;
    private ILogger<PlayoutBuilder> _logger;

    public PlayoutBuilder(
        IConfigElementRepository configElementRepository,
        IMediaCollectionRepository mediaCollectionRepository,
        ITelevisionRepository televisionRepository,
        IArtistRepository artistRepository,
        IMultiEpisodeShuffleCollectionEnumeratorFactory multiEpisodeFactory,
        IFileSystem fileSystem,
        IRerunHelper rerunHelper,
        ILogger<PlayoutBuilder> logger)
    {
        _configElementRepository = configElementRepository;
        _mediaCollectionRepository = mediaCollectionRepository;
        _televisionRepository = televisionRepository;
        _artistRepository = artistRepository;
        _multiEpisodeFactory = multiEpisodeFactory;
        _fileSystem = fileSystem;
        _rerunHelper = rerunHelper;
        _logger = logger;
    }

    public bool TrimStart { get; set; } = true;

    public Playlist DebugPlaylist
    {
        get => _debugPlaylist;
        set
        {
            if (value is not null)
            {
                _debugPlaylist = value;
                _logger = NullLogger<PlayoutBuilder>.Instance;
            }
        }
    }

    public async Task<Either<BaseError, PlayoutBuildResult>> Build(
        DateTimeOffset start,
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        if (playout.ScheduleKind is not PlayoutScheduleKind.Classic)
        {
            _logger.LogWarning(
                "Skipping playout build with type {Type} on channel {Number} - {Name}",
                playout.ScheduleKind,
                referenceData.Channel.Number,
                referenceData.Channel.Name);

            return BaseError.New($"Cannot build playout type {playout.ScheduleKind} using classic playout builder.");
        }

        Either<BaseError, PlayoutParameters> validateResult = await Validate(start, referenceData, cancellationToken);
        return await validateResult.MatchAsync(
            async parameters =>
            {
                // for testing purposes
                // if (mode == PlayoutBuildMode.Reset)
                // {
                //     return await Build(playout, mode, parameters with { Start = parameters.Start.AddDays(-2) });
                // }

                Either<BaseError, PlayoutBuildResult> buildResult = await Build(
                    playout,
                    referenceData,
                    PlayoutBuildResult.Empty,
                    mode,
                    parameters,
                    cancellationToken);

                return buildResult.Match(
                    result => result with
                    {
                        RerunHistoryToRemove = _rerunHelper.GetHistoryToRemove(),
                        AddedRerunHistory = _rerunHelper.GetHistoryToAdd()
                    },
                    Either<BaseError, PlayoutBuildResult>.Left);
            },
            Either<BaseError, PlayoutBuildResult>.Left);
    }

    private Task<Either<BaseError, PlayoutBuildResult>> Build(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutBuildMode mode,
        PlayoutParameters parameters,
        CancellationToken cancellationToken) =>
        mode switch
        {
            PlayoutBuildMode.Refresh => RefreshPlayout(playout, referenceData, result, parameters, cancellationToken),
            PlayoutBuildMode.Reset => ResetPlayout(playout, referenceData, result, parameters, cancellationToken),
            _ => ContinuePlayout(playout, referenceData, result, parameters, cancellationToken)
        };

    internal async Task<Either<BaseError, PlayoutBuildResult>> Build(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutBuildMode mode,
        DateTimeOffset start,
        DateTimeOffset finish,
        CancellationToken cancellationToken)
    {
        Either<BaseError, PlayoutParameters> validateResult = await Validate(start, referenceData, cancellationToken);
        return await validateResult.MatchAsync(
            async parameters => await Build(
                playout,
                referenceData,
                result,
                mode,
                parameters with { Start = start, Finish = finish },
                cancellationToken),
            Either<BaseError, PlayoutBuildResult>.Left);
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> RefreshPlayout(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutParameters parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Refreshing playout {PlayoutId} for channel {ChannelNumber} - {ChannelName}",
            playout.Id,
            referenceData.Channel.Number,
            referenceData.Channel.Name);

        result = result with { ClearItems = true };
        playout.Anchor = null;

        // foreach (PlayoutProgramScheduleAnchor anchor in playout.ProgramScheduleAnchors)
        // {
        //     anchor.Playout = null;
        //     anchor.Collection = null;
        //     anchor.ProgramSchedule = null;
        // }

        // _logger.LogDebug("All anchors: {@Anchors}", playout.ProgramScheduleAnchors);

        // remove null anchor date ("continue" anchors)
        playout.ProgramScheduleAnchors.RemoveAll(a => a.AnchorDate is null);

        // _logger.LogDebug("Checkpoint anchors: {@Anchors}", playout.ProgramScheduleAnchors);

        // remove old checkpoints
        playout.ProgramScheduleAnchors.RemoveAll(a =>
            a.AnchorDateOffset.IfNone(SystemTime.MaxValueUtc) < parameters.Start.Date);

        // remove new checkpoints
        playout.ProgramScheduleAnchors.RemoveAll(a =>
            a.AnchorDateOffset.IfNone(SystemTime.MinValueUtc).Date > parameters.Start.Date);

        // _logger.LogDebug("Remaining anchors: {@Anchors}", playout.ProgramScheduleAnchors);

        var allAnchors = playout.ProgramScheduleAnchors.ToList();

        var collectionIds = playout.ProgramScheduleAnchors.Map(a => Optional(a.CollectionId)).Somes().ToHashSet();

        var multiCollectionIds =
            playout.ProgramScheduleAnchors.Map(a => Optional(a.MultiCollectionId)).Somes().ToHashSet();

        var smartCollectionIds =
            playout.ProgramScheduleAnchors.Map(a => Optional(a.SmartCollectionId)).Somes().ToHashSet();

        var searchQueries =
            playout.ProgramScheduleAnchors.Map(a => Optional(a.SearchQuery)).Somes().ToHashSet();

        var rerunCollectionIds =
            playout.ProgramScheduleAnchors.Map(a => Optional(a.RerunCollectionId)).Somes().ToHashSet();

        var mediaItemIds = playout.ProgramScheduleAnchors.Map(a => Optional(a.MediaItemId)).Somes().ToHashSet();

        playout.ProgramScheduleAnchors.Clear();

        foreach (int collectionId in collectionIds)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.CollectionId == collectionId)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        foreach (int multiCollectionId in multiCollectionIds)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.MultiCollectionId == multiCollectionId)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        foreach (int smartCollectionId in smartCollectionIds)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.SmartCollectionId == smartCollectionId)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        foreach (string searchQuery in searchQueries)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.SearchQuery == searchQuery)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        foreach (int rerunCollectionId in rerunCollectionIds)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.RerunCollectionId == rerunCollectionId)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        foreach (int mediaItemId in mediaItemIds)
        {
            PlayoutProgramScheduleAnchor minAnchor = allAnchors.Filter(a => a.MediaItemId == mediaItemId)
                .MinBy(a => a.AnchorDateOffset.IfNone(DateTimeOffset.MaxValue).Ticks);
            playout.ProgramScheduleAnchors.Add(minAnchor);
        }

        // _logger.LogDebug("Oldest anchors for each collection: {@Anchors}", playout.ProgramScheduleAnchors);

        // convert checkpoints to non-checkpoints
        // foreach (PlayoutProgramScheduleAnchor anchor in playout.ProgramScheduleAnchors)
        // {
        //     anchor.AnchorDate = null;
        // }

        // _logger.LogDebug("Final anchors: {@Anchors}", playout.ProgramScheduleAnchors);

        Option<DateTime> maybeAnchorDate = playout.ProgramScheduleAnchors
            .Map(a => Optional(a.AnchorDate))
            .Somes()
            .HeadOrNone();

        foreach (DateTime anchorDate in maybeAnchorDate)
        {
            playout.Anchor = new PlayoutAnchor
            {
                NextStart = anchorDate
            };
        }

        return await BuildPlayoutItems(
            playout,
            referenceData,
            result,
            parameters.Start,
            parameters.Finish,
            parameters.CollectionMediaItems,
            false,
            cancellationToken);
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> ResetPlayout(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutParameters parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Resetting playout {PlayoutId} for channel {ChannelNumber} - {ChannelName}",
            playout.Id,
            referenceData.Channel.Number,
            referenceData.Channel.Name);

        result = result with { ClearItems = true };
        _rerunHelper.ClearHistory = true;
        playout.Anchor = null;
        playout.ProgramScheduleAnchors.Clear();
        playout.OnDemandCheckpoint = null;
        playout.Seed = new Random().Next();

        // don't trim start for on demand channels, we want to time shift it all forward
        // on-demand channels will trim already-played items in time shifter
        if (referenceData.Channel.PlayoutMode is ChannelPlayoutMode.OnDemand)
        {
            TrimStart = false;
        }

        Either<BaseError, PlayoutBuildResult> maybeResult = await BuildPlayoutItems(
            playout,
            referenceData,
            result,
            parameters.Start,
            parameters.Finish,
            parameters.CollectionMediaItems,
            true,
            cancellationToken);

        foreach (BaseError error in maybeResult.LeftToSeq())
        {
            return error;
        }

        foreach (PlayoutBuildResult r in maybeResult.RightToSeq())
        {
            result = r;
        }

        // time shift on demand channel if needed
        if (referenceData.Channel.PlayoutMode is ChannelPlayoutMode.OnDemand)
        {
            result = result with { TimeShiftTo = parameters.Start };
        }

        return result;
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> ContinuePlayout(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutParameters parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Building playout {PlayoutId} for channel {ChannelNumber} - {ChannelName}",
            playout.Id,
            referenceData.Channel.Number,
            referenceData.Channel.Name);

        // remove old checkpoints
        playout.ProgramScheduleAnchors.RemoveAll(a =>
            a.AnchorDateOffset.IfNone(SystemTime.MaxValueUtc) < parameters.Start.Date);

        // _logger.LogDebug("Remaining anchors: {@Anchors}", playout.ProgramScheduleAnchors);

        return await BuildPlayoutItems(
            playout,
            referenceData,
            result,
            parameters.Start,
            parameters.Finish,
            parameters.CollectionMediaItems,
            false,
            cancellationToken);
    }

    private async Task<Either<BaseError, PlayoutParameters>> Validate(
        DateTimeOffset start,
        PlayoutReferenceData referenceData,
        CancellationToken cancellationToken)
    {
        if (referenceData.ProgramSchedule.Items.Count == 0)
        {
            _logger.LogWarning("Playout {Playout}'s schedule has no schedule items", referenceData.Channel.Name);
            return BaseError.New($"Playout {referenceData.Channel.Name}'s schedule has no schedule items");
        }

        Map<CollectionKey, List<MediaItem>> collectionMediaItems =
            await GetCollectionMediaItems(referenceData, cancellationToken);
        if (collectionMediaItems.IsEmpty)
        {
            _logger.LogWarning("Playout {Playout} has no items", referenceData.Channel.Name);
            return BaseError.New($"Playout {referenceData.Channel.Name} has no items");
        }

        Option<bool> skipMissingItems =
            await _configElementRepository.GetValue<bool>(ConfigElementKey.PlayoutSkipMissingItems, cancellationToken);

        Option<CollectionKey> maybeEmptyCollection = await CheckForEmptyCollections(
            collectionMediaItems,
            await skipMissingItems.IfNoneAsync(false));

        foreach (CollectionKey emptyCollection in maybeEmptyCollection)
        {
            Option<string> maybeName =
                await _mediaCollectionRepository.GetNameFromKey(emptyCollection, cancellationToken);
            return maybeName.Match(
                name =>
                {
                    _logger.LogError(
                        "Unable to rebuild playout; {CollectionType} \"{CollectionName}\" has no valid items!",
                        emptyCollection.CollectionType,
                        name);

                    return BaseError.New(
                        $"Unable to rebuild playout; {emptyCollection.CollectionType} \"{name}\" has no valid items!");
                },
                () =>
                {
                    _logger.LogError(
                        "Unable to rebuild playout; collection {@CollectionKey} has no valid items!",
                        emptyCollection);

                    return BaseError.New(
                        $"Unable to rebuild playout; collection {HistoryDetails.KeyForCollectionKey(emptyCollection)} has no valid items!");
                });
        }

        Option<int> daysToBuild = await _configElementRepository.GetValue<int>(
            ConfigElementKey.PlayoutDaysToBuild,
            cancellationToken);

        DateTimeOffset now = start;

        return new PlayoutParameters(
            now,
            now.AddDays(await daysToBuild.IfNoneAsync(2)),
            collectionMediaItems);
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> BuildPlayoutItems(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        DateTimeOffset playoutStart,
        DateTimeOffset playoutFinish,
        Map<CollectionKey, List<MediaItem>> collectionMediaItems,
        bool randomStartPoint,
        CancellationToken cancellationToken)
    {
        DateTimeOffset trimBefore = playoutStart.AddHours(-4);
        DateTimeOffset trimAfter = playoutFinish;

        DateTimeOffset start = playoutStart;
        DateTimeOffset finish = playoutStart.Date.AddDays(1);

        // _logger.LogDebug(
        //     "Trim before: {TrimBefore}, Start: {Start}, Finish: {Finish}, PlayoutFinish: {PlayoutFinish}",
        //     trimBefore,
        //     start,
        //     finish,
        //     playoutFinish);

        if (playout.Anchor is not null && playout.Anchor.NextStartOffset > playoutFinish)
        {
            // nothing to do
            return result;
        }

        // build each day with "continue" anchors
        while (finish < playoutFinish)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BaseError.New("Playout build was canceled");
            }

            _logger.LogDebug("Building playout from {Start} to {Finish}", start, finish);
            Either<BaseError, PlayoutBuildResult> buildResult = await BuildPlayoutItems(
                playout,
                referenceData,
                result,
                start,
                finish,
                collectionMediaItems,
                true,
                randomStartPoint,
                cancellationToken);

            foreach (BaseError error in buildResult.LeftToSeq())
            {
                return error;
            }

            foreach (PlayoutBuildResult r in buildResult.RightToSeq())
            {
                result = r;
            }

            // only randomize once (at the start of the playout)
            randomStartPoint = false;

            start = playout.Anchor?.NextStartOffset ?? start;
            finish = finish.AddDays(1);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return BaseError.New("Playout build was canceled");
        }

        if (start < playoutFinish)
        {
            // build one final time without continue anchors
            _logger.LogDebug("Building final playout from {Start} to {Finish}", start, playoutFinish);
            Either<BaseError, PlayoutBuildResult> buildResult = await BuildPlayoutItems(
                playout,
                referenceData,
                result,
                start,
                playoutFinish,
                collectionMediaItems,
                false,
                randomStartPoint,
                cancellationToken);

            foreach (BaseError error in buildResult.LeftToSeq())
            {
                return error;
            }

            foreach (PlayoutBuildResult r in buildResult.RightToSeq())
            {
                result = r;
            }
        }

        if (TrimStart)
        {
            // remove old items
            result = result with { RemoveBefore = trimBefore };
        }

        // on demand channels end up with slightly more than expected due to time shifting from midnight to first build
        if (referenceData.Channel.PlayoutMode is not ChannelPlayoutMode.OnDemand)
        {
            // check for future items that aren't grouped inside range
            var futureItems = result.AddedItems.Filter(i => i.StartOffset > trimAfter).ToList();
            int futureItemCount = futureItems.Count(futureItem =>
                result.AddedItems.All(i => i == futureItem || i.GuideGroup != futureItem.GuideGroup));

            // it feels hacky to have to clean up a playlist like this,
            // so only log the warning, and leave the bad data to fail tests
            // playout.Items.Remove(futureItem);

            if (futureItemCount > 0)
            {
                _logger.LogInformation(
                    "{Count} playout items are scheduled after hard stop of {HardStop}; this is expected if duration is used.",
                    futureItemCount,
                    trimAfter);
            }
        }

        return result;
    }

    private async Task<Either<BaseError, PlayoutBuildResult>> BuildPlayoutItems(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        DateTimeOffset playoutStart,
        DateTimeOffset playoutFinish,
        Map<CollectionKey, List<MediaItem>> collectionMediaItems,
        bool saveAnchorDate,
        bool randomStartPoint,
        CancellationToken cancellationToken)
    {
        var random = new Random(playout.Seed);

        ProgramSchedule activeSchedule = AlternateScheduleSelector
            .GetScheduleForDate(referenceData.ProgramScheduleAlternates, playoutStart)
            .Match(s => s.ProgramSchedule, referenceData.ProgramSchedule);

        if (activeSchedule.Items.Count == 0)
        {
            // empty schedule results in empty day
            playout.Anchor = new PlayoutAnchor { NextStart = playoutFinish.UtcDateTime };
            return result;
        }

        // on demand channels do NOT use alternate schedules
        if (referenceData.Channel.PlayoutMode is ChannelPlayoutMode.OnDemand)
        {
            activeSchedule = referenceData.ProgramSchedule;
        }

        // _logger.LogDebug("Active schedule is: {Schedule}", activeSchedule.Name);

        // random start points are disabled in some scenarios, so ensure it's enabled and active
        randomStartPoint = randomStartPoint && activeSchedule.RandomStartPoint;

        var sortedScheduleItems = activeSchedule.Items.OrderBy(i => i.Index).ToList();
        CollectionEnumeratorState scheduleItemsEnumeratorState =
            playout.Anchor?.ScheduleItemsEnumeratorState ?? new CollectionEnumeratorState
                { Seed = random.Next(), Index = 0 };
        IScheduleItemsEnumerator scheduleItemsEnumerator = activeSchedule.ShuffleScheduleItems
            ? new ShuffledScheduleItemsEnumerator(activeSchedule.Items, scheduleItemsEnumeratorState)
            : new OrderedScheduleItemsEnumerator(activeSchedule.Items, scheduleItemsEnumeratorState);

        var collectionEnumerators = new Dictionary<CollectionKey, IMediaCollectionEnumerator>();
        foreach ((CollectionKey collectionKey, List<MediaItem> mediaItems) in collectionMediaItems)
        {
            // use configured playback order for primary collection, shuffle for filler
            Option<ProgramScheduleItem> maybeScheduleItem = sortedScheduleItems
                .FirstOrDefault(item => CollectionKey.ForScheduleItem(item) == collectionKey);
            foreach (var scheduleItem in maybeScheduleItem)
            {
                IMediaCollectionEnumerator enumerator =
                    await GetMediaCollectionEnumerator(
                        playout,
                        activeSchedule,
                        collectionKey,
                        mediaItems,
                        scheduleItem.PlaybackOrder,
                        scheduleItem.MarathonGroupBy,
                        scheduleItem.MarathonShuffleGroups,
                        scheduleItem.MarathonShuffleItems,
                        scheduleItem.MarathonBatchSize,
                        randomStartPoint,
                        random,
                        cancellationToken);

                collectionEnumerators.Add(collectionKey, enumerator);
            }

            // filler
            if (maybeScheduleItem.IsNone)
            {
                IMediaCollectionEnumerator enumerator =
                    await GetMediaCollectionEnumerator(
                        playout,
                        activeSchedule,
                        collectionKey,
                        mediaItems,
                        PlaybackOrder.Shuffle,
                        MarathonGroupBy.None,
                        marathonShuffleGroups: false,
                        marathonShuffleItems: false,
                        marathonBatchSize: null,
                        randomStartPoint,
                        random,
                        cancellationToken);

                collectionEnumerators.Add(collectionKey, enumerator);
            }
        }

        var collectionItemCount = collectionMediaItems.Map((k, v) => (k, v.Count)).Values.ToDictionary();

        var scheduleItemsFillGroupEnumerators = new Dictionary<int, IScheduleItemsEnumerator>();
        foreach (ProgramScheduleItem scheduleItem in sortedScheduleItems.Where(si =>
                     si.FillWithGroupMode is not FillWithGroupMode.None))
        {
            var collectionKey = CollectionKey.ForScheduleItem(scheduleItem);
            List<MediaItem> mediaItems = await MediaItemsForCollection.Collect(
                _mediaCollectionRepository,
                _televisionRepository,
                _artistRepository,
                collectionKey,
                cancellationToken);
            string collectionKeyString = JsonConvert.SerializeObject(
                collectionKey,
                Formatting.None,
                new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
            collectionKeyString = $"{scheduleItem.Id}:{collectionKeyString}";
            var fakeCollections = _mediaCollectionRepository.GroupIntoFakeCollections(mediaItems, collectionKeyString)
                .Filter(c => c.ShowId > 0 || c.ArtistId > 0 || !string.IsNullOrWhiteSpace(c.Key))
                .ToList();
            List<ProgramScheduleItem> fakeScheduleItems = [];
            foreach (CollectionWithItems fakeCollection in fakeCollections)
            {
                CollectionKey key = (fakeCollection.ShowId, fakeCollection.ArtistId, fakeCollection.Key) switch
                {
                    var (showId, _, _) when showId > 0 => new CollectionKey
                    {
                        CollectionType = CollectionType.TelevisionShow,
                        MediaItemId = showId,
                        FakeCollectionKey = collectionKeyString
                    },
                    var (_, artistId, _) when artistId > 0 => new CollectionKey
                    {
                        CollectionType = CollectionType.Artist,
                        MediaItemId = artistId,
                        FakeCollectionKey = collectionKeyString
                    },
                    var (_, _, k) when k is not null => new CollectionKey
                    {
                        CollectionType = CollectionType.FakeCollection,
                        FakeCollectionKey = collectionKeyString
                    },
                    var (_, _, _) => null
                };

                if (key is null)
                {
                    continue;
                }

                var copyScheduleItem = scheduleItem.DeepCopy();
                copyScheduleItem.CollectionType = key.CollectionType;
                copyScheduleItem.MediaItemId = key.MediaItemId;
                copyScheduleItem.FakeCollectionKey = key.FakeCollectionKey;
                fakeScheduleItems.Add(copyScheduleItem);

                IMediaCollectionEnumerator enumerator = await GetMediaCollectionEnumerator(
                    playout,
                    activeSchedule,
                    key,
                    fakeCollection.MediaItems,
                    scheduleItem.PlaybackOrder,
                    scheduleItem.MarathonGroupBy,
                    scheduleItem.MarathonShuffleGroups,
                    scheduleItem.MarathonShuffleItems,
                    scheduleItem.MarathonBatchSize,
                    randomStartPoint,
                    random,
                    cancellationToken);

                collectionEnumerators.Add(key, enumerator);

                // this makes multiple (0) work - since it needs the number of items in the collection
                collectionItemCount.Add(key, fakeCollection.MediaItems.Count);
            }

            CollectionEnumeratorState enumeratorState =
                playout.FillGroupIndices.Any(fgi => fgi.ProgramScheduleItemId == scheduleItem.Id)
                    ? playout.FillGroupIndices.Find(fgi => fgi.ProgramScheduleItemId == scheduleItem.Id).EnumeratorState
                    : new CollectionEnumeratorState { Seed = random.Next() + scheduleItem.Id, Index = 0 };

            switch (scheduleItem.FillWithGroupMode)
            {
                case FillWithGroupMode.FillWithOrderedGroups:
                {
                    var enumerator = new OrderedScheduleItemsEnumerator(fakeScheduleItems, enumeratorState);
                    scheduleItemsFillGroupEnumerators[scheduleItem.Id] = enumerator;
                    break;
                }
                case FillWithGroupMode.FillWithShuffledGroups:
                {
                    var enumerator = new ShuffledScheduleItemsEnumerator(fakeScheduleItems, enumeratorState);
                    scheduleItemsFillGroupEnumerators[scheduleItem.Id] = enumerator;
                    break;
                }
            }
        }

        // find start anchor
        PlayoutAnchor startAnchor = FindStartAnchor(playout, playoutStart, scheduleItemsEnumerator);

        // clear duration finish if it has already passed
        foreach (DateTimeOffset durationFinish in startAnchor.DurationFinishOffset)
        {
            if (durationFinish < startAnchor.NextStartOffset)
            {
                startAnchor.DurationFinish = null;
            }
        }

        // _logger.LogDebug("Start anchor: {@StartAnchor}", startAnchor);

        // start at the previously-decided time
        DateTimeOffset currentTime = startAnchor.NextStartOffset.ToLocalTime();

        if (currentTime >= playoutFinish)
        {
            // nothing to do, no need to add more anchors
            return result;
        }

        // _logger.LogDebug(
        //     "Starting playout ({PlayoutId}) for channel {ChannelNumber} - {ChannelName} at {StartTime}",
        //     playout.Id,
        //     referenceData.Channel.Number,
        //     referenceData.Channel.Name,
        //     currentTime);

        // removing any items scheduled past the start anchor
        // this could happen if the app was closed after scheduling items
        // but before saving the anchor
        foreach (var item in referenceData.ExistingItems.Where(i => i.Start >= currentTime))
        {
            result.ItemsToRemove.Add(item.Id);
        }

        // start with the previously-decided schedule item
        // start with the previous multiple/duration states
        var playoutBuilderState = new PlayoutBuilderState(
            playout.Id,
            scheduleItemsEnumerator,
            Optional(startAnchor.MultipleRemaining),
            startAnchor.DurationFinishOffset,
            startAnchor.InFlood,
            startAnchor.InDurationFiller,
            startAnchor.NextGuideGroup,
            currentTime);

        var schedulerOne = new PlayoutModeSchedulerOne(_logger);
        var schedulerMultiple = new PlayoutModeSchedulerMultiple(collectionItemCount.ToMap(), _logger);
        var schedulerDuration = new PlayoutModeSchedulerDuration(_logger);
        var schedulerFlood = new PlayoutModeSchedulerFlood(_logger);

        var timeCount = new Dictionary<DateTimeOffset, int>();

        // loop until we're done filling the desired amount of time
        while (playoutBuilderState.CurrentTime < playoutFinish && !cancellationToken.IsCancellationRequested)
        {
            if (timeCount.TryGetValue(playoutBuilderState.CurrentTime, out int count))
            {
                timeCount[playoutBuilderState.CurrentTime] = count + 1;
            }
            else
            {
                timeCount[playoutBuilderState.CurrentTime] = 1;
            }

            if (timeCount[playoutBuilderState.CurrentTime] == 6)
            {
                _logger.LogWarning(
                    "Failed to schedule beyond {Time}; aborting playout build - this can be caused by an impossible schedule, or by a bug",
                    playoutBuilderState.CurrentTime);

                return new SchedulingLoopEncountered();
            }

            // _logger.LogDebug("Playout time is {CurrentTime}", playoutBuilderState.CurrentTime);

            // get the schedule item out of the sorted list
            ProgramScheduleItem scheduleItem = playoutBuilderState.ScheduleItemsEnumerator.Current;

            // replace with the fake schedule item when filling with group
            if (scheduleItem.FillWithGroupMode is not FillWithGroupMode.None)
            {
                scheduleItem = scheduleItemsFillGroupEnumerators[scheduleItem.Id].Current;
            }

            ProgramScheduleItem nextScheduleItem = playoutBuilderState.ScheduleItemsEnumerator.Peek(1);

            PlayoutSchedulerResult schedulerResult = scheduleItem switch
            {
                ProgramScheduleItemMultiple multiple => schedulerMultiple.Schedule(
                    playoutBuilderState,
                    collectionEnumerators,
                    multiple,
                    nextScheduleItem,
                    playoutFinish,
                    random,
                    cancellationToken),
                ProgramScheduleItemDuration duration => schedulerDuration.Schedule(
                    playoutBuilderState,
                    collectionEnumerators,
                    duration,
                    nextScheduleItem,
                    playoutFinish,
                    random,
                    cancellationToken),
                ProgramScheduleItemFlood flood => schedulerFlood.Schedule(
                    playoutBuilderState,
                    collectionEnumerators,
                    flood,
                    nextScheduleItem,
                    playoutFinish,
                    random,
                    cancellationToken),
                ProgramScheduleItemOne one => schedulerOne.Schedule(
                    playoutBuilderState,
                    collectionEnumerators,
                    one,
                    nextScheduleItem,
                    playoutFinish,
                    random,
                    cancellationToken),
                _ => throw new NotSupportedException(nameof(scheduleItem))
            };

            (PlayoutBuilderState nextState, List<PlayoutItem> playoutItems, PlayoutBuildWarnings warnings) =
                schedulerResult;

            result.Warnings.Merge(warnings);

            // if we completed a multiple/duration block, move to the next fill group
            if (scheduleItem.FillWithGroupMode is not FillWithGroupMode.None)
            {
                if (nextState.MultipleRemaining.IsNone && nextState.DurationFinish.IsNone)
                {
                    scheduleItemsFillGroupEnumerators[scheduleItem.Id].MoveNext();
                }
            }

            // if (playoutItems.Count > 0 && result.AddedItems.Count > 0)
            // {
            //     var gap = playoutItems.Min(pi => pi.StartOffset) - result.AddedItems.Max(pi => pi.FinishOffset);
            //     if (gap > TimeSpan.FromHours(1))
            //     {
            //         _logger.LogWarning(
            //             "Large gap at {CurrentTime} ({Gap}) when scheduling item from schedule {Name} index {Index}",
            //             playoutBuilderState.CurrentTime,
            //             gap,
            //             activeSchedule.Name,
            //             scheduleItem.Index);
            //
            //         _logger.LogWarning(
            //             "Start type: {StartType}, start time: {StartTime}, fixed start time behavior: {FixedStartTimeBehavior}",
            //             scheduleItem.StartType,
            //             scheduleItem.StartTime,
            //             scheduleItem.FixedStartTimeBehavior ?? activeSchedule.FixedStartTimeBehavior);
            //     }
            // }

            result.AddedItems.AddRange(playoutItems);

            playoutBuilderState = nextState;
        }

        // once more to get playout anchor
        ProgramScheduleItem anchorScheduleItem = playoutBuilderState.ScheduleItemsEnumerator.Current;

        if (result.AddedItems.Count != 0)
        {
            DateTimeOffset maxStartTime = result.AddedItems.Max(i => i.FinishOffset);
            if (maxStartTime < playoutBuilderState.CurrentTime)
            {
                playoutBuilderState = playoutBuilderState with { CurrentTime = maxStartTime };
            }
        }

        playout.Anchor = new PlayoutAnchor
        {
            ScheduleItemsEnumeratorState = playoutBuilderState.ScheduleItemsEnumerator.State,
            NextStart = PlayoutModeSchedulerBase<ProgramScheduleItem>
                .GetStartTimeAfter(playoutBuilderState, anchorScheduleItem, Option<ILogger>.None)
                .UtcDateTime,
            InFlood = playoutBuilderState.InFlood,
            InDurationFiller = playoutBuilderState.InDurationFiller,
            NextGuideGroup = playoutBuilderState.NextGuideGroup
        };

        foreach (int multipleRemaining in playoutBuilderState.MultipleRemaining)
        {
            playout.Anchor.MultipleRemaining = multipleRemaining;
        }

        foreach (DateTimeOffset durationFinish in playoutBuilderState.DurationFinish)
        {
            // don't save duration finish if it already passed
            if (durationFinish > playout.Anchor.NextStartOffset)
            {
                playout.Anchor.DurationFinish = durationFinish.UtcDateTime;
            }
        }

        ProgramSchedule activeScheduleAtAnchor = AlternateScheduleSelector
            .GetScheduleForDate(referenceData.ProgramScheduleAlternates, playoutBuilderState.CurrentTime)
            .Match(s => s.ProgramSchedule, referenceData.ProgramSchedule);

        // if we ended in a different alternate schedule, fix the anchor data
        if (playoutBuilderState.CurrentTime >= playoutFinish && activeScheduleAtAnchor.Id != activeSchedule.Id &&
            activeScheduleAtAnchor.Items.Count > 0)
        {
            PlayoutBuilderState cleanState = playoutBuilderState with
            {
                InFlood = false,
                InDurationFiller = false,
                MultipleRemaining = Option<int>.None,
                DurationFinish = Option<DateTimeOffset>.None
            };

            var firstItem = activeScheduleAtAnchor.Items.OrderBy(i => i.Index).Head();
            DateTimeOffset nextStart = PlayoutModeSchedulerBase<ProgramScheduleItem>.GetStartTimeAfter(
                cleanState,
                firstItem,
                Option<ILogger>.Some(_logger));

            if (playoutBuilderState.CurrentTime.TimeOfDay > TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "Playout build went beyond midnight ({Time}) into a different alternate schedule; this may cause issues with start times on the next day",
                    playoutBuilderState.CurrentTime);
            }

            // TimeSpan gap = nextStart - playoutBuilderState.CurrentTime;
            // var fixedStartTimeBehavior =
            //     firstItem.FixedStartTimeBehavior ?? activeScheduleAtAnchor.FixedStartTimeBehavior;
            //
            // if (gap > TimeSpan.FromHours(1) && firstItem.StartTime.HasValue && fixedStartTimeBehavior == FixedStartTimeBehavior.Strict)
            // {
            //     _logger.LogWarning(
            //         "Offline playout gap of {Gap} caused by strict fixed start time {StartTime} before current time {CurrentTime} on schedule {Name}",
            //         gap.Humanize(),
            //         firstItem.StartTime.Value,
            //         playoutBuilderState.CurrentTime.TimeOfDay,
            //         activeScheduleAtAnchor.Name);
            // }

            playout.Anchor.NextStart = nextStart.UtcDateTime;
            playout.Anchor.InFlood = false;
            playout.Anchor.InDurationFiller = false;
            playout.Anchor.MultipleRemaining = null;
            playout.Anchor.DurationFinish = null;
            playout.Anchor.ScheduleItemsEnumeratorState = new CollectionEnumeratorState
            {
                Seed = playoutBuilderState.ScheduleItemsEnumerator.State.Seed,
                Index = 0
            };
        }

        // build program schedule anchors
        playout.ProgramScheduleAnchors = BuildProgramScheduleAnchors(playout, collectionEnumerators, saveAnchorDate);

        // build fill group indices
        playout.FillGroupIndices = BuildFillGroupIndices(playout, scheduleItemsFillGroupEnumerators);

        return result;
    }

    private static List<PlayoutScheduleItemFillGroupIndex> BuildFillGroupIndices(
        Playout playout,
        Dictionary<int, IScheduleItemsEnumerator> scheduleItemsFillGroupEnumerators)
    {
        var result = playout.FillGroupIndices.ToList();

        foreach ((int programScheduleItemId, IScheduleItemsEnumerator enumerator) in scheduleItemsFillGroupEnumerators)
        {
            Option<PlayoutScheduleItemFillGroupIndex> maybeFgi = Optional(
                result.FirstOrDefault(fgi => fgi.ProgramScheduleItemId == programScheduleItemId));

            foreach (PlayoutScheduleItemFillGroupIndex fgi in maybeFgi)
            {
                fgi.EnumeratorState = enumerator.State;
            }

            if (maybeFgi.IsNone)
            {
                var fgi = new PlayoutScheduleItemFillGroupIndex
                {
                    PlayoutId = playout.Id,
                    ProgramScheduleItemId = programScheduleItemId,
                    EnumeratorState = enumerator.State
                };

                result.Add(fgi);
            }
        }

        return result;
    }

    private async Task<Map<CollectionKey, List<MediaItem>>> GetCollectionMediaItems(
        PlayoutReferenceData referenceData,
        CancellationToken cancellationToken)
    {
        IEnumerable<KeyValuePair<CollectionKey, Option<FillerPreset>>> collectionKeys =
            GetAllCollectionKeys(referenceData);

        IEnumerable<Task<KeyValuePair<CollectionKey, List<MediaItem>>>> tasks = collectionKeys.Select(async key =>
        {
            List<MediaItem> mediaItems = await FetchMediaItemsForKeyAsync(key.Key, key.Value, cancellationToken);
            return new KeyValuePair<CollectionKey, List<MediaItem>>(key.Key, mediaItems);
        });

        return Map.createRange(await Task.WhenAll(tasks));
    }

    private static IEnumerable<KeyValuePair<CollectionKey, Option<FillerPreset>>> GetAllCollectionKeys(
        PlayoutReferenceData referenceData) =>
        referenceData.ProgramSchedule.Items
            .Append(referenceData.ProgramScheduleAlternates.Bind(psa => psa.ProgramSchedule.Items))
            .DistinctBy(item => item.Id)
            .SelectMany(CollectionKeysForItem)
            .DistinctBy(kvp => kvp.Key);

    private async Task<List<MediaItem>> FetchMediaItemsForKeyAsync(
        CollectionKey collectionKey,
        Option<FillerPreset> fillerPreset,
        CancellationToken cancellationToken)
    {
        List<MediaItem> result = await MediaItemsForCollection.Collect(
            _mediaCollectionRepository,
            _televisionRepository,
            _artistRepository,
            collectionKey,
            cancellationToken);

        foreach (FillerPreset _ in fillerPreset.Where(p => p.UseChaptersAsMediaItems))
        {
            var fakeResults = new List<MediaItem>();
            var uniqueId = 1;

            foreach (MediaItem mediaItem in result)
            {
                MediaVersion version = mediaItem.GetHeadVersion();
                var allChapters = Optional(version.Chapters).Flatten().OrderBy(c => c.StartTime).ToList();
                if (allChapters.Count > 0)
                {
                    foreach (MediaChapter chapter in allChapters)
                    {
                        var chapterVersion = new ChapterMediaVersion(chapter);
                        var chapterItem = new ChapterMediaItem(uniqueId++, mediaItem, chapterVersion);
                        fakeResults.Add(chapterItem);
                    }
                }
                else
                {
                    // still use a fake item here so we don't have id conflicts
                    var chapterVersion = new ChapterMediaVersion(
                        new MediaChapter { StartTime = TimeSpan.Zero, EndTime = version.Duration });
                    var chapterItem = new ChapterMediaItem(uniqueId++, mediaItem, chapterVersion);
                    fakeResults.Add(chapterItem);
                }
            }

            return fakeResults;
        }

        return result;
    }

    private async Task<Option<CollectionKey>> CheckForEmptyCollections(
        Map<CollectionKey, List<MediaItem>> collectionMediaItems,
        bool skipMissingItems)
    {
        foreach ((CollectionKey _, List<MediaItem> items) in collectionMediaItems)
        {
            var zeroItems = new List<MediaItem>();
            var missingItems = new List<MediaItem>();

            foreach (MediaItem item in items)
            {
                bool isZero = item switch
                {
                    Movie m => await m.MediaVersions.Map(v => v.Duration).HeadOrNone()
                        .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero,
                    Episode e => await e.MediaVersions.Map(v => v.Duration).HeadOrNone()
                        .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero,
                    MusicVideo mv => await mv.MediaVersions.Map(v => v.Duration).HeadOrNone()
                        .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero,
                    OtherVideo ov => await ov.MediaVersions.Map(v => v.Duration).HeadOrNone()
                        .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero,
                    Song s => await s.MediaVersions.Map(v => v.Duration).HeadOrNone()
                        .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero,
                    Image => false,
                    RemoteStream rs => await rs.MediaVersions.Map(v => v.Duration).HeadOrNone()
                                           .IfNoneAsync(TimeSpan.Zero) == TimeSpan.Zero
                                       && (!rs.Duration.HasValue || rs.Duration.Value == TimeSpan.Zero),
                    ChapterMediaItem c => c.MediaVersion.Duration == TimeSpan.Zero,
                    _ => true
                };

                if (skipMissingItems && item.State is MediaItemState.FileNotFound or MediaItemState.Unavailable)
                {
                    _logger.LogWarning(
                        "Skipping media item that does not exist on disk  {MediaItem} - {MediaItemTitle} - {Path}",
                        item.Id,
                        DisplayTitle(item),
                        item.GetHeadVersion().MediaFiles.Head().Path);

                    missingItems.Add(item);
                }
                else if (isZero)
                {
                    _logger.LogWarning(
                        "Skipping media item with zero duration {MediaItem} - {MediaItemTitle}",
                        item.Id,
                        DisplayTitle(item));

                    zeroItems.Add(item);
                }
            }

            items.RemoveAll(missingItems.Contains);
            items.RemoveAll(zeroItems.Contains);
        }

        return collectionMediaItems.Find(c => c.Value.Count == 0).Map(c => c.Key);
    }

    private static PlayoutAnchor FindStartAnchor(
        Playout playout,
        DateTimeOffset start,
        IScheduleItemsEnumerator enumerator) =>
        Optional(playout.Anchor).IfNone(() =>
        {
            ProgramScheduleItem schedule = enumerator.Current;
            switch (schedule.StartType)
            {
                case StartType.Fixed:
                    return new PlayoutAnchor
                    {
                        ScheduleItemsEnumeratorState = enumerator.State,
                        NextStart = (start - start.TimeOfDay).UtcDateTime +
                                    schedule.StartTime.GetValueOrDefault()
                    };
                case StartType.Dynamic:
                default:
                    return new PlayoutAnchor
                    {
                        ScheduleItemsEnumeratorState = enumerator.State,
                        NextStart = (start - start.TimeOfDay).UtcDateTime
                    };
            }
        });

    private static List<PlayoutProgramScheduleAnchor> BuildProgramScheduleAnchors(
        Playout playout,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        bool saveAnchorDate)
    {
        var result = new List<PlayoutProgramScheduleAnchor>();

        foreach (CollectionKey collectionKey in collectionEnumerators.Keys)
        {
            Option<PlayoutProgramScheduleAnchor> maybeExisting = playout.ProgramScheduleAnchors.FirstOrDefault(a =>
                a.CollectionType == collectionKey.CollectionType
                && a.CollectionId == collectionKey.CollectionId
                && a.MediaItemId == collectionKey.MediaItemId
                && a.FakeCollectionKey == collectionKey.FakeCollectionKey
                && a.SmartCollectionId == collectionKey.SmartCollectionId
                && a.RerunCollectionId == collectionKey.RerunCollectionId
                && a.MultiCollectionId == collectionKey.MultiCollectionId
                && a.PlaylistId == collectionKey.PlaylistId
                && a.SearchQuery == collectionKey.SearchQuery
                && a.AnchorDate is null);

            var maybeEnumeratorState = collectionEnumerators.ToDictionary(e => e.Key, e => e.Value.State);

            PlayoutProgramScheduleAnchor scheduleAnchor = maybeExisting.Match(
                existing =>
                {
                    existing.EnumeratorState = maybeEnumeratorState[collectionKey];
                    return existing;
                },
                () => new PlayoutProgramScheduleAnchor
                {
                    Playout = playout,
                    PlayoutId = playout.Id,
                    CollectionType = collectionKey.CollectionType,
                    CollectionId = collectionKey.CollectionId,
                    MultiCollectionId = collectionKey.MultiCollectionId,
                    SmartCollectionId = collectionKey.SmartCollectionId,
                    RerunCollectionId = collectionKey.RerunCollectionId,
                    MediaItemId = collectionKey.MediaItemId,
                    PlaylistId = collectionKey.PlaylistId,
                    SearchQuery = collectionKey.SearchQuery,
                    FakeCollectionKey = collectionKey.FakeCollectionKey,
                    EnumeratorState = maybeEnumeratorState[collectionKey]
                });

            if (saveAnchorDate)
            {
                scheduleAnchor.AnchorDate = playout.Anchor?.NextStart;
            }

            result.Add(scheduleAnchor);
        }

        foreach (PlayoutProgramScheduleAnchor checkpointAnchor in playout.ProgramScheduleAnchors.Where(a =>
                     a.AnchorDate is not null))
        {
            result.Add(checkpointAnchor);
        }

        return result;
    }

    private async Task<IMediaCollectionEnumerator> GetMediaCollectionEnumerator(
        Playout playout,
        ProgramSchedule activeSchedule,
        CollectionKey collectionKey,
        List<MediaItem> mediaItems,
        PlaybackOrder playbackOrder,
        MarathonGroupBy marathonGroupBy,
        bool marathonShuffleGroups,
        bool marathonShuffleItems,
        int? marathonBatchSize,
        bool randomStartPoint,
        Random random,
        CancellationToken cancellationToken)
    {
        Option<PlayoutProgramScheduleAnchor> maybeAnchor = playout.ProgramScheduleAnchors
            .OrderByDescending(a => a.AnchorDate ?? DateTime.MaxValue)
            .FirstOrDefault(a => a.CollectionType == collectionKey.CollectionType
                                 && a.CollectionId == collectionKey.CollectionId
                                 && a.MultiCollectionId == collectionKey.MultiCollectionId
                                 && a.SmartCollectionId == collectionKey.SmartCollectionId
                                 && a.RerunCollectionId == collectionKey.RerunCollectionId
                                 && a.MediaItemId == collectionKey.MediaItemId
                                 && a.PlaylistId == collectionKey.PlaylistId
                                 && a.SearchQuery == collectionKey.SearchQuery);

        CollectionEnumeratorState state = null;

        foreach (PlayoutProgramScheduleAnchor anchor in maybeAnchor)
        {
            // _logger.LogDebug("Selecting anchor {@Anchor}", anchor);

            anchor.EnumeratorState ??= new CollectionEnumeratorState { Seed = random.Next(), Index = 0 };

            state = anchor.EnumeratorState;
        }

        state ??= new CollectionEnumeratorState { Seed = random.Next(), Index = 0 };

        if (collectionKey.CollectionType is CollectionType.RerunFirstRun or CollectionType.RerunRerun)
        {
            await _rerunHelper.InitWithMediaItems(playout.Id, collectionKey, mediaItems, cancellationToken);
            return _rerunHelper.CreateEnumerator(collectionKey, state, cancellationToken);
        }

        if (collectionKey.CollectionType is CollectionType.Playlist)
        {
            foreach (int playlistId in Optional(collectionKey.PlaylistId))
            {
                Dictionary<PlaylistItem, List<MediaItem>> playlistItemMap = DebugPlaylist is not null
                    ? await _mediaCollectionRepository.GetPlaylistItemMap(DebugPlaylist, cancellationToken)
                    : await _mediaCollectionRepository.GetPlaylistItemMap(playlistId, cancellationToken);

                return await PlaylistEnumerator.Create(
                    _mediaCollectionRepository,
                    playlistItemMap,
                    state,
                    marathonShuffleGroups,
                    batchSize: Option<int>.None,
                    cancellationToken);
            }
        }

        int collectionId = collectionKey.CollectionId ?? 0;

        if (collectionKey.CollectionType == CollectionType.Collection &&
            await _mediaCollectionRepository.IsCustomPlaybackOrder(collectionId))
        {
            Option<Collection> maybeCollectionWithItems =
                await _mediaCollectionRepository.GetCollectionWithCollectionItemsUntracked(collectionId);

            foreach (Collection collectionWithItems in maybeCollectionWithItems)
            {
                return new CustomOrderCollectionEnumerator(
                    collectionWithItems,
                    mediaItems,
                    state);
            }
        }

        // index shouldn't ever be greater than zero with randomStartPoint since anchors shouldn't exist, but
        randomStartPoint = randomStartPoint && state.Index == 0;

        switch (playbackOrder)
        {
            case PlaybackOrder.Chronological:
                if (randomStartPoint)
                {
                    state = new CollectionEnumeratorState
                    {
                        Seed = state.Seed,
                        Index = random.Next(0, mediaItems.Count - 1)
                    };
                }

                return new ChronologicalMediaCollectionEnumerator(mediaItems, state);
            case PlaybackOrder.SeasonEpisode:
                if (randomStartPoint)
                {
                    state = new CollectionEnumeratorState
                    {
                        Seed = state.Seed,
                        Index = random.Next(0, mediaItems.Count - 1)
                    };
                }

                return new SeasonEpisodeMediaCollectionEnumerator(mediaItems, state);
            case PlaybackOrder.Random:
                return new RandomizedMediaCollectionEnumerator(mediaItems, state);
            case PlaybackOrder.ShuffleInOrder:
                return new ShuffleInOrderCollectionEnumerator(
                    await GetCollectionItemsForShuffleInOrder(
                        _mediaCollectionRepository,
                        collectionKey,
                        cancellationToken),
                    state,
                    activeSchedule.RandomStartPoint,
                    cancellationToken);
            case PlaybackOrder.MultiEpisodeShuffle when
                collectionKey.CollectionType == CollectionType.TelevisionShow &&
                collectionKey.MediaItemId.HasValue:
                foreach (Show show in await _televisionRepository.GetShow(collectionKey.MediaItemId.Value, cancellationToken))
                {
                    foreach (MetadataGuid guid in show.ShowMetadata.Map(sm => sm.Guids).Flatten())
                    {
                        string jsScriptPath = Path.ChangeExtension(
                            Path.Combine(
                                FileSystemLayout.MultiEpisodeShuffleTemplatesFolder,
                                guid.Guid.Replace("://", "_")),
                            "js");
                        _logger.LogDebug("Checking for JS Script at {Path}", jsScriptPath);
                        if (_fileSystem.File.Exists(jsScriptPath))
                        {
                            _logger.LogDebug("Found JS Script at {Path}", jsScriptPath);
                            try
                            {
                                return _multiEpisodeFactory.Create(jsScriptPath, mediaItems, state, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    ex,
                                    "Failed to initialize multi-episode shuffle; falling back to normal shuffle");
                            }
                        }
                    }
                }

                // fall back to shuffle if show or template cannot be found
                goto case PlaybackOrder.Shuffle;

            // fall back to shuffle when television show isn't selected
            case PlaybackOrder.MultiEpisodeShuffle:
            case PlaybackOrder.Shuffle:
                return new ShuffledMediaCollectionEnumerator(
                    await GetGroupedMediaItemsForShuffle(
                        _mediaCollectionRepository,
                        activeSchedule,
                        mediaItems,
                        collectionKey,
                        cancellationToken),
                    state,
                    cancellationToken);

            case PlaybackOrder.Marathon:
                var helper = new MarathonHelper(_mediaCollectionRepository);
                Option<PlaylistEnumerator> maybeEnumerator = await helper.GetEnumerator(
                    mediaItems,
                    marathonGroupBy,
                    marathonShuffleGroups,
                    marathonShuffleItems,
                    marathonBatchSize is > 0 ? marathonBatchSize.Value : Option<int>.None,
                    state,
                    cancellationToken);

                foreach (var enumerator in maybeEnumerator)
                {
                    return enumerator;
                }

                // fall through to default case if we can't make the proper enumerator
                goto default;

            default:
                // TODO: handle this error case differently?
                return new RandomizedMediaCollectionEnumerator(mediaItems, state);
        }
    }

    internal static async Task<List<GroupedMediaItem>> GetGroupedMediaItemsForShuffle(
        IMediaCollectionRepository mediaCollectionRepository,
        ProgramSchedule activeSchedule,
        List<MediaItem> mediaItems,
        CollectionKey collectionKey,
        CancellationToken cancellationToken)
    {
        if (collectionKey.MultiCollectionId != null)
        {
            List<CollectionWithItems> collections = await mediaCollectionRepository
                .GetMultiCollectionCollections(collectionKey.MultiCollectionId.Value, cancellationToken);

            return MultiCollectionGrouper.GroupMediaItems(collections);
        }

        return activeSchedule.KeepMultiPartEpisodesTogether
            ? MultiPartEpisodeGrouper.GroupMediaItems(mediaItems, activeSchedule.TreatCollectionsAsShows)
            : mediaItems.Map(mi => new GroupedMediaItem(mi, null)).ToList();
    }

    internal static async Task<List<CollectionWithItems>> GetCollectionItemsForShuffleInOrder(
        IMediaCollectionRepository mediaCollectionRepository,
        CollectionKey collectionKey,
        CancellationToken cancellationToken)
    {
        List<CollectionWithItems> result;

        if (collectionKey.MultiCollectionId != null)
        {
            result = await mediaCollectionRepository.GetMultiCollectionCollections(
                collectionKey.MultiCollectionId.Value,
                cancellationToken);
        }
        else
        {
            result = await mediaCollectionRepository.GetFakeMultiCollectionCollections(
                collectionKey.CollectionId,
                collectionKey.SmartCollectionId,
                cancellationToken);
        }

        return result;
    }

    internal static string DisplayTitle(MediaItem mediaItem)
    {
        switch (mediaItem)
        {
            case Episode e:
                string showTitle = e.Season.Show.ShowMetadata.HeadOrNone()
                    .Map(sm => $"{sm.Title} - ").IfNone(string.Empty);
                return e.EpisodeMetadata.HeadOrNone()
                    .Map(em => $"{showTitle}s{e.Season.SeasonNumber:00}e{em.EpisodeNumber:00} - {em.Title}")
                    .IfNone("[unknown episode]");
            case Movie m:
                return m.MovieMetadata.HeadOrNone().Match(mm => mm.Title ?? string.Empty, () => "[unknown movie]");
            case MusicVideo mv:
                string artistName = mv.Artist.ArtistMetadata.HeadOrNone()
                    .Map(am => $"{am.Title} - ").IfNone(string.Empty);
                return mv.MusicVideoMetadata.HeadOrNone()
                    .Map(mvm => $"{artistName}{mvm.Title}")
                    .IfNone("[unknown music video]");
            case OtherVideo ov:
                return ov.OtherVideoMetadata.HeadOrNone().Match(
                    ovm => ovm.Title ?? string.Empty,
                    () => "[unknown video]");
            case Song s:
                return s.SongMetadata.HeadOrNone().Match(
                    sm => sm.Title ?? string.Empty,
                    () => "[unknown song]");
            case Image i:
                return i.ImageMetadata.HeadOrNone().Match(
                    sm => sm.Title ?? string.Empty,
                    () => "[unknown image]");
            case RemoteStream rs:
                return rs.RemoteStreamMetadata.HeadOrNone().Match(
                    sm => sm.Title ?? string.Empty,
                    () => "[unknown remote stream]");
            default:
                return string.Empty;
        }
    }

    private static List<KeyValuePair<CollectionKey, Option<FillerPreset>>> CollectionKeysForItem(
        ProgramScheduleItem item)
    {
        var result = new List<KeyValuePair<CollectionKey, Option<FillerPreset>>>
        {
            new(CollectionKey.ForScheduleItem(item), Option<FillerPreset>.None)
        };

        if (item.PreRollFiller != null)
        {
            result.Add(
                new KeyValuePair<CollectionKey, Option<FillerPreset>>(
                    CollectionKey.ForFillerPreset(item.PreRollFiller),
                    item.PreRollFiller));
        }

        if (item.MidRollFiller != null)
        {
            result.Add(
                new KeyValuePair<CollectionKey, Option<FillerPreset>>(
                    CollectionKey.ForFillerPreset(item.MidRollFiller),
                    item.MidRollFiller));
        }

        if (item.PostRollFiller != null)
        {
            result.Add(
                new KeyValuePair<CollectionKey, Option<FillerPreset>>(
                    CollectionKey.ForFillerPreset(item.PostRollFiller),
                    item.PostRollFiller));
        }

        if (item.TailFiller != null)
        {
            result.Add(
                new KeyValuePair<CollectionKey, Option<FillerPreset>>(
                    CollectionKey.ForFillerPreset(item.TailFiller),
                    item.TailFiller));
        }

        if (item.FallbackFiller != null)
        {
            result.Add(
                new KeyValuePair<CollectionKey, Option<FillerPreset>>(
                    CollectionKey.ForFillerPreset(item.FallbackFiller),
                    item.FallbackFiller));
        }

        return result;
    }
}
