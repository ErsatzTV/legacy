using System.Globalization;
using System.IO.Abstractions;
using System.IO.Enumeration;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg.Selector;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NCalc;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Core.FFmpeg;

public class CustomStreamSelector(IFileSystem fileSystem, ILogger<CustomStreamSelector> logger)
    : ICustomStreamSelector
{
    public async Task<StreamSelectorResult> SelectStreams(
        Channel channel,
        DateTimeOffset contentStartTime,
        MediaItemAudioVersion audioVersion,
        List<Subtitle> allSubtitles,
        bool shouldLogMessages)
    {
        ILogger<CustomStreamSelector> log = shouldLogMessages ? logger : NullLogger<CustomStreamSelector>.Instance;

        try
        {
            string streamSelectorFile = Path.Combine(
                FileSystemLayout.ChannelStreamSelectorsFolder,
                channel.StreamSelector);

            if (!fileSystem.File.Exists(streamSelectorFile))
            {
                log.LogWarning("YAML stream selector file {File} does not exist; aborting.", channel.StreamSelector);
                return StreamSelectorResult.None;
            }

            StreamSelector streamSelector = await LoadStreamSelector(streamSelectorFile, log);

            var audioStreams = audioVersion.MediaVersion.Streams
                .Where(s => s.MediaStreamKind == MediaStreamKind.Audio)
                .ToList();

            foreach (StreamSelectorItem streamSelectorItem in streamSelector.Items)
            {
                if (!string.IsNullOrWhiteSpace(streamSelectorItem.ContentCondition))
                {
                    if (!ContentMatchesCondition(channel, contentStartTime, streamSelectorItem.ContentCondition))
                    {
                        log.LogDebug(
                            "Content does not match selector item {@SelectorItem}",
                            streamSelectorItem);
                        continue;
                    }
                }

                var candidateAudioStreams = audioStreams.ToDictionary(a => a, _ => int.MaxValue);
                var candidateSubtitles = allSubtitles.ToDictionary(s => s, _ => int.MaxValue);

                var passesAudio = false;
                var passesSubtitles = false;

                // try to find matching audio stream
                foreach (MediaStream audioStream in audioStreams.ToList())
                {
                    var matches = false;
                    string safeTitle = (audioStream.Title ?? string.Empty).ToLowerInvariant();
                    string safeLanguage = (audioStream.Language ?? "und").ToLowerInvariant();

                    if (streamSelectorItem.AudioLanguages.Count > 0)
                    {
                        // match any of the listed languages
                        for (var langIndex = 0; langIndex < streamSelectorItem.AudioLanguages.Count; langIndex++)
                        {
                            string audioLanguage = streamSelectorItem.AudioLanguages[langIndex];

                            // special case
                            if (audioLanguage == "*")
                            {
                                matches = true;
                            }

                            matches = matches || FileSystemName.MatchesSimpleExpression(
                                audioLanguage.ToLowerInvariant(),
                                safeLanguage);

                            // store lang index for prioritizing later
                            if (matches && candidateAudioStreams[audioStream] == int.MaxValue)
                            {
                                candidateAudioStreams[audioStream] = langIndex;
                            }
                        }
                    }
                    else
                    {
                        matches = true;
                    }

                    if (streamSelectorItem.AudioTitleBlocklist
                        .Any(block => safeTitle.Contains(block, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches = false;
                    }

                    if (streamSelectorItem.AudioTitleAllowlist.Count > 0)
                    {
                        int matchCount = streamSelectorItem.AudioTitleAllowlist
                            .Count(block => safeTitle.Contains(block, StringComparison.OrdinalIgnoreCase));

                        if (matchCount == 0)
                        {
                            matches = false;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(streamSelectorItem.AudioCondition))
                    {
                        if (!AudioMatchesCondition(audioStream, streamSelectorItem.AudioCondition))
                        {
                            matches = false;
                        }
                    }

                    if (!matches)
                    {
                        candidateAudioStreams.Remove(audioStream);

                        log.LogDebug(
                            "Audio stream {@Stream} does not match selector item {@SelectorItem}",
                            new { Language = safeLanguage, Title = safeTitle },
                            streamSelectorItem);
                    }
                    else
                    {
                        passesAudio = true;

                        log.LogDebug(
                            "Audio stream {@Stream} matches selector item {@SelectorItem}",
                            new { Language = safeLanguage, Title = safeTitle },
                            streamSelectorItem);
                    }
                }

                // try to find matching subtitle stream
                if (streamSelectorItem.DisableSubtitles)
                {
                    candidateSubtitles.Clear();
                    passesSubtitles = true;
                }
                else
                {
                    foreach (Subtitle subtitle in allSubtitles.ToList())
                    {
                        var matches = false;
                        string safeTitle = (subtitle.Title ?? string.Empty).ToLowerInvariant();
                        string safeLanguage = (subtitle.Language ?? "und").ToLowerInvariant();

                        if (streamSelectorItem.SubtitleLanguages.Count > 0)
                        {
                            // match any of the listed languages
                            for (var langIndex = 0; langIndex < streamSelectorItem.SubtitleLanguages.Count; langIndex++)
                            {
                                string subtitleLanguage = streamSelectorItem.SubtitleLanguages[langIndex];

                                // special case
                                if (subtitleLanguage == "*")
                                {
                                    matches = true;
                                }

                                matches = matches || FileSystemName.MatchesSimpleExpression(
                                    subtitleLanguage,
                                    safeLanguage);

                                // store lang index for prioritizing later
                                if (matches && candidateSubtitles[subtitle] == int.MaxValue)
                                {
                                    candidateSubtitles[subtitle] = langIndex;
                                }
                            }
                        }
                        else
                        {
                            matches = true;
                        }

                        if (streamSelectorItem.SubtitleTitleBlocklist
                            .Any(block => safeTitle.Contains(block, StringComparison.OrdinalIgnoreCase)))
                        {
                            matches = false;
                        }

                        if (streamSelectorItem.SubtitleTitleAllowlist.Count > 0)
                        {
                            int matchCount = streamSelectorItem.SubtitleTitleAllowlist
                                .Count(block => safeTitle.Contains(block, StringComparison.OrdinalIgnoreCase));

                            if (matchCount == 0)
                            {
                                matches = false;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(streamSelectorItem.SubtitleCondition))
                        {
                            if (!SubtitleMatchesCondition(subtitle, streamSelectorItem.SubtitleCondition))
                            {
                                matches = false;
                            }
                        }

                        // HLS Direct doesn't need to extract; Next streaming engine doesn't need to extract
                        if (channel.StreamingMode != StreamingMode.HttpLiveStreamingDirect &&
                            channel.StreamingEngine != StreamingEngine.Next &&
                            subtitle.SubtitleKind is SubtitleKind.Embedded && !subtitle.IsImage &&
                            !subtitle.IsExtracted)
                        {
                            candidateSubtitles.Remove(subtitle);

                            log.LogDebug(
                                "Subtitle {@Subtitle} is embedded text subtitle and NOT extracted; ignoring",
                                new { Language = safeLanguage, Title = safeTitle });
                        }
                        else if (!matches)
                        {
                            candidateSubtitles.Remove(subtitle);

                            log.LogDebug(
                                "Subtitle {@Subtitle} does not match selector item {@SelectorItem}",
                                new { Language = safeLanguage, Title = safeTitle },
                                streamSelectorItem);
                        }
                        else
                        {
                            passesSubtitles = true;

                            log.LogDebug(
                                "Subtitle {@Subtitle} matches selector item {@SelectorItem}",
                                new { Language = safeLanguage, Title = safeTitle },
                                streamSelectorItem);
                        }
                    }
                }

                Option<MediaStream> maybeAudioStream = candidateAudioStreams
                    .OrderBy(a => a.Value)
                    .Select(a => a.Key)
                    .HeadOrNone();

                Option<Subtitle> maybeSubtitle = candidateSubtitles
                    .OrderBy(s => s.Value)
                    .Select(s => s.Key)
                    .HeadOrNone();

                if (passesAudio && passesSubtitles)
                {
                    return new StreamSelectorResult(maybeAudioStream, maybeSubtitle);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unexpected error selecting streams");
        }

        return StreamSelectorResult.None;
    }

    private static bool AudioMatchesCondition(MediaStream audioStream, string audioCondition)
    {
        var expression = new Expression(audioCondition);
        expression.EvaluateParameter += (name, e) =>
        {
            e.Result = name switch
            {
                "id" => audioStream.Index,
                "title" => (audioStream.Title ?? string.Empty).ToLowerInvariant(),
                "lang" => (audioStream.Language ?? string.Empty).ToLowerInvariant(),
                "default" => audioStream.Default,
                "forced" => audioStream.Forced,
                "codec" => (audioStream.Codec ?? string.Empty).ToLowerInvariant(),
                "channels" => audioStream.Channels,
                _ => e.Result
            };
        };

        return expression.Evaluate() as bool? == true;
    }

    private static bool SubtitleMatchesCondition(Subtitle subtitle, string subtitleCondition)
    {
        var expression = new Expression(subtitleCondition);
        expression.EvaluateParameter += (name, e) =>
        {
            e.Result = name switch
            {
                "id" => subtitle.StreamIndex,
                "title" => (subtitle.Title ?? string.Empty).ToLowerInvariant(),
                "lang" => (subtitle.Language ?? string.Empty).ToLowerInvariant(),
                "default" => subtitle.Default,
                "forced" => subtitle.Forced,
                "sdh" => subtitle.SDH,
                "codec" => (subtitle.Codec ?? string.Empty).ToLowerInvariant(),
                "external" => subtitle.SubtitleKind is SubtitleKind.Sidecar,
                _ => e.Result
            };
        };

        return expression.Evaluate() as bool? == true;
    }

    private static bool ContentMatchesCondition(
        Channel channel,
        DateTimeOffset contentStartTime,
        string contentCondition)
    {
        var expression = new Expression(contentCondition);
        expression.EvaluateParameter += (name, e) =>
        {
            e.Result = name switch
            {
                "channel_number" => channel.Number,
                "channel_name" => channel.Name,
                "time_of_day_seconds" => contentStartTime.TimeOfDay.TotalSeconds,
                "day_of_week" => GetLocalizedDayOfWeekIndex(contentStartTime),
                _ => e.Result
            };
        };

        return expression.Evaluate() as bool? == true;
    }

    private async Task<StreamSelector> LoadStreamSelector(string streamSelectorFile, ILogger<CustomStreamSelector> log)
    {
        try
        {
            string yaml = await fileSystem.File.ReadAllTextAsync(streamSelectorFile);

            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            return deserializer.Deserialize<StreamSelector>(yaml);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Error loading YAML stream selector");
            throw;
        }
    }

    private static int GetLocalizedDayOfWeekIndex(DateTimeOffset date)
    {
        DayOfWeek first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        DayOfWeek current = date.DayOfWeek;
        return ((int)current - (int)first + 7) % 7;
    }
}
