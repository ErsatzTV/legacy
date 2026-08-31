using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public abstract class YamlPlayoutContentHandler(EnumeratorCache enumeratorCache) : IYamlPlayoutHandler
{
    public bool Reset => false;

    public abstract Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken);

    protected async Task<Option<IMediaCollectionEnumerator>> GetContentEnumerator(
        YamlPlayoutContext context,
        string contentKey,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentKey))
        {
            return Option<IMediaCollectionEnumerator>.None;
        }

        Option<IMediaCollectionEnumerator> maybeEnumerator = await enumeratorCache.GetCachedEnumeratorForContent(
            context,
            contentKey,
            cancellationToken);

        if (maybeEnumerator.IsNone)
        {
            if (!enumeratorCache.MissingContentKeys.Contains(contentKey))
            {
                logger.LogWarning("Unable to locate content with key {Key}", contentKey);
                enumeratorCache.MissingContentKeys.Add(contentKey);
            }
        }

        return maybeEnumerator;
    }

    protected static List<PlayoutHistory> GetHistoryForItem(
        YamlPlayoutContext context,
        string contentKey,
        IMediaCollectionEnumerator enumerator,
        PlayoutItem playoutItem,
        MediaItem mediaItem,
        ILogger<SequentialPlayoutBuilder> logger)
    {
        int index = context.Definition.Content.FindIndex(c => c.Key == contentKey);
        if (index < 0)
        {
            logger.LogDebug("Unable to find history for content matching key {ContentKey}", contentKey);
            return [];
        }

        YamlPlayoutContentItem contentItem = context.Definition.Content[index];
        if (!Enum.TryParse(contentItem.Order, true, out PlaybackOrder playbackOrder))
        {
            logger.LogDebug(
                "Unable to find history for content matching playback order {PlaybackOrder}",
                contentItem.Order);
            return [];
        }

        var result = new List<PlayoutHistory>();

        string historyKey = HistoryDetails.KeyForYamlContent(contentItem);

        if (enumerator is PlaylistEnumerator playlistEnumerator)
        {
            // create a playout history record
            var nextHistory = new PlayoutHistory
            {
                PlayoutId = context.Playout.Id,
                PlaybackOrder = playbackOrder,
                Index = playlistEnumerator.State.Index,
                Seed = playlistEnumerator.State.Seed,
                When = playoutItem.StartOffset.UtcDateTime,
                Finish = playoutItem.FinishOffset.UtcDateTime,
                Key = historyKey,
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
                        PlayoutId = context.Playout.Id,
                        PlaybackOrder = playbackOrder,
                        Index = childEnumerator.State.Index,
                        Seed = childEnumerator.State.Seed,
                        When = playoutItem.StartOffset.UtcDateTime,
                        Finish = playoutItem.FinishOffset.UtcDateTime,
                        Key = historyKey,
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
                PlayoutId = context.Playout.Id,
                PlaybackOrder = playbackOrder,
                Index = enumerator.State.Index,
                When = playoutItem.StartOffset.UtcDateTime,
                Finish = playoutItem.FinishOffset.UtcDateTime,
                Key = historyKey,
                Details = HistoryDetails.ForMediaItem(mediaItem)
            };

            result.Add(nextHistory);
        }

        return result;
    }

    protected static FillerKind GetFillerKind(YamlPlayoutInstruction instruction, YamlPlayoutContext context)
    {
        if (!string.IsNullOrWhiteSpace(instruction.FillerKind) &&
            Enum.TryParse(instruction.FillerKind, true, out FillerKind result))
        {
            return result;
        }

        foreach (FillerKind fillerKind in context.GetFillerKind())
        {
            return fillerKind;
        }

        return FillerKind.None;
    }

    protected static List<MediaChapter> ChaptersForMediaItem(MediaItem mediaItem)
    {
        MediaVersion version = mediaItem.GetHeadVersion();
        return Optional(version.Chapters).Flatten().OrderBy(c => c.StartTime).ToList();
    }

    protected static async Task AddItemAndMidRoll(
        YamlPlayoutContext context,
        PlayoutItem playoutItem,
        MediaItem mediaItem,
        Func<string, Task> executeSequence)
    {
        List<MediaChapter> itemChapters = ChaptersForMediaItem(mediaItem);
        Option<YamlPlayoutContext.MidRollSequence> maybeMidRollSequence = context.GetMidRollSequence();
        if (itemChapters.Count < 2 || maybeMidRollSequence.IsNone)
        {
            context.AddedItems.Add(playoutItem);
            context.CurrentTime += playoutItem.OutPoint - playoutItem.InPoint;
        }
        else
        {
            foreach (YamlPlayoutContext.MidRollSequence midRollSequence in maybeMidRollSequence)
            {
                List<MediaChapter> filteredChapters = FillerExpression.FilterChapters(
                    midRollSequence.Expression,
                    itemChapters,
                    playoutItem);

                if (filteredChapters.Count < 2)
                {
                    context.AddedItems.Add(playoutItem);
                    context.CurrentTime += playoutItem.OutPoint - playoutItem.InPoint;
                }
                else
                {
                    for (var j = 0; j < filteredChapters.Count; j++)
                    {
                        PlayoutItem nextItem = playoutItem.ForChapter(filteredChapters[j]);

                        nextItem.Start = context.CurrentTime.UtcDateTime;
                        nextItem.Finish = context.CurrentTime.UtcDateTime + (nextItem.OutPoint - nextItem.InPoint);
                        context.AddedItems.Add(nextItem);

                        context.CurrentTime += nextItem.OutPoint - nextItem.InPoint;

                        if (j < filteredChapters.Count - 1)
                        {
                            context.PushFillerKind(FillerKind.MidRoll);
                            await executeSequence(midRollSequence.Sequence);
                            context.PopFillerKind();
                        }
                    }
                }
            }
        }
    }
}
