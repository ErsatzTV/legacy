using ErsatzTV.Core.Domain;
using Microsoft.Extensions.Logging;
using NCalc;

namespace ErsatzTV.Core.Scheduling;

public static class FillerExpression
{
    public static List<MediaChapter> FilterChapters(
        string fillerExpression,
        List<MediaChapter> effectiveChapters,
        PlayoutItem playoutItem,
        string fillerName,
        ILogger logger)
    {
        if (effectiveChapters.Count == 0 || string.IsNullOrWhiteSpace(fillerExpression))
        {
            return effectiveChapters;
        }

        var candidateChapters = effectiveChapters.SkipLast().ToList();

        var newChapters = new List<MediaChapter>();

        TimeSpan start = effectiveChapters.Select(c => c.StartTime).Min();
        TimeSpan end = effectiveChapters.Select(c => c.EndTime).Max();

        double lastFiller = start.TotalSeconds - 99999.0;
        double contentDuration = (playoutItem.FinishOffset - playoutItem.StartOffset).TotalSeconds;
        var matches = 0;

        for (var index = 0; index < candidateChapters.Count; index++)
        {
            MediaChapter chapter = candidateChapters[index];
            TimeSpan chapterPoint = chapter.EndTime;
            var expression = new Expression(fillerExpression, ExpressionOptions.CaseInsensitiveStringComparer);
            int chapterNum = index + 1;
            double sinceLastFiller = chapterPoint.TotalSeconds - lastFiller;
            int matchedPoints = matches;
            expression.EvaluateParameter += (name, e) =>
            {
                e.Result = name switch
                {
                    "last_mid_filler" => sinceLastFiller,
                    "total_points" => effectiveChapters.Count - 1,
                    "total_duration" => contentDuration,
                    "total_progress" => chapterPoint.TotalSeconds / end.TotalSeconds,
                    "remaining_duration" => contentDuration - chapterPoint.TotalSeconds,
                    "matched_points" => matchedPoints,
                    "point" => chapterPoint.TotalSeconds,
                    "num" => chapterNum,
                    "title" => chapter.Title ?? string.Empty,
                    _ => throw new ArgumentException($"Unknown parameter {name}")
                };
            };

            bool matched;
            try
            {
                matched = expression.Evaluate() as bool? == true;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Filler expression {Expression} for {FillerName} is invalid; no mid-roll will be used: {Error}",
                    fillerExpression,
                    fillerName,
                    ex.Message);

                // one chapter is the "no points" result; an empty list would drop the content duration from padding
                return [new MediaChapter { StartTime = start, EndTime = end }];
            }

            if (matched)
            {
                matches += 1;
                lastFiller = chapterPoint.TotalSeconds;

                // copy so the media item's own chapters keep their times for later playout items
                MediaChapter source = effectiveChapters[index];
                newChapters.Add(
                    new MediaChapter
                    {
                        Id = source.Id,
                        MediaVersionId = source.MediaVersionId,
                        ChapterId = source.ChapterId,
                        StartTime = source.StartTime,
                        EndTime = source.EndTime,
                        Title = source.Title
                    });
            }
        }

        if (newChapters.Count > 0)
        {
            TimeSpan currentTime = start;
            foreach (MediaChapter chapter in newChapters)
            {
                chapter.StartTime = currentTime;
                currentTime = chapter.EndTime;
            }

            newChapters.Add(new MediaChapter { StartTime = currentTime, EndTime = end });
        }

        return newChapters;
    }
}
