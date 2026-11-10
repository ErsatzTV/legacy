using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutCountHandler(EnumeratorCache enumeratorCache) : YamlPlayoutContentHandler(enumeratorCache)
{
    public override async Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutCountInstruction count)
        {
            return false;
        }

        Option<IMediaCollectionEnumerator> maybeEnumerator = await GetContentEnumerator(
            context,
            instruction.Content,
            logger,
            cancellationToken);

        foreach (IMediaCollectionEnumerator enumerator in maybeEnumerator)
        {
            int seed = context.Playout.Seed + context.InstructionIndex + context.CurrentTime.DayOfYear;
            var random = new Random(seed);
            int countValue = CountExpression.Evaluate(count.Count, enumerator, random, cancellationToken);

            for (var i = 0; i < countValue; i++)
            {
                foreach (string preRollSequence in context.GetPreRollSequence())
                {
                    context.PushFillerKind(FillerKind.PreRoll);
                    await executeSequence(preRollSequence);
                    context.PopFillerKind();
                }

                foreach (MediaItem mediaItem in enumerator.Current)
                {
                    TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                    // create a playout item
                    var playoutItem = new PlayoutItem
                    {
                        PlayoutId = context.Playout.Id,
                        MediaItemId = mediaItem.Id,
                        Start = context.CurrentTime.UtcDateTime,
                        Finish = context.CurrentTime.UtcDateTime + itemDuration,
                        InPoint = TimeSpan.Zero,
                        OutPoint = itemDuration,
                        FillerKind = GetFillerKind(count, context),
                        CustomTitle = string.IsNullOrWhiteSpace(count.CustomTitle) ? null : count.CustomTitle,
                        DisableWatermarks = count.DisableWatermarks,
                        //PreferredAudioLanguageCode = scheduleItem.PreferredAudioLanguageCode,
                        //PreferredAudioTitle = scheduleItem.PreferredAudioTitle,
                        //PreferredSubtitleLanguageCode = scheduleItem.PreferredSubtitleLanguageCode,
                        //SubtitleMode = scheduleItem.SubtitleMode
                        GuideGroup = context.PeekNextGuideGroup(),
                        //GuideStart = effectiveBlock.Start.UtcDateTime,
                        //GuideFinish = blockFinish.UtcDateTime,
                        //BlockKey = JsonConvert.SerializeObject(effectiveBlock.BlockKey),
                        //CollectionKey = JsonConvert.SerializeObject(collectionKey, JsonSettings),
                        //CollectionEtag = collectionEtags[collectionKey],
                        PlayoutItemWatermarks = [],
                        PlayoutItemGraphicsElements = []
                    };

                    foreach (int watermarkId in context.GetChannelWatermarkIds())
                    {
                        playoutItem.PlayoutItemWatermarks.Add(
                            new PlayoutItemWatermark
                            {
                                PlayoutItem = playoutItem,
                                WatermarkId = watermarkId
                            });
                    }

                    foreach ((int graphicsElementId, string variablesJson) in context.GetGraphicsElements())
                    {
                        playoutItem.PlayoutItemGraphicsElements.Add(
                            new PlayoutItemGraphicsElement
                            {
                                PlayoutItem = playoutItem,
                                GraphicsElementId = graphicsElementId,
                                Variables = variablesJson
                            });
                    }

                    await AddItemAndMidRoll(context, playoutItem, mediaItem, executeSequence, logger);
                    context.AdvanceGuideGroup();

                    // create history record
                    List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                        context,
                        instruction.Content,
                        enumerator,
                        playoutItem,
                        mediaItem,
                        logger);

                    foreach (PlayoutHistory history in maybeHistory)
                    {
                        context.AddedHistory.Add(history);
                    }

                    enumerator.MoveNext(playoutItem.StartOffset);
                }

                foreach (string postRollSequence in context.GetPostRollSequence())
                {
                    context.PushFillerKind(FillerKind.PostRoll);
                    await executeSequence(postRollSequence);
                    context.PopFillerKind();
                }
            }

            return true;
        }

        return false;
    }
}
