using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scripting;
using ErsatzTV.Core.Scripting;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.FFmpeg;

public class FFmpegStreamSelector : IFFmpegStreamSelector
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IFileSystem _fileSystem;
    private readonly ILanguageCodeService _languageCodeService;
    private readonly ILogger<FFmpegStreamSelector> _logger;
    private readonly IScriptEngine _scriptEngine;
    private readonly IStreamSelectorRepository _streamSelectorRepository;

    public FFmpegStreamSelector(
        IScriptEngine scriptEngine,
        IStreamSelectorRepository streamSelectorRepository,
        IConfigElementRepository configElementRepository,
        IFileSystem fileSystem,
        ILanguageCodeService languageCodeService,
        ILogger<FFmpegStreamSelector> logger)
    {
        _scriptEngine = scriptEngine;
        _streamSelectorRepository = streamSelectorRepository;
        _configElementRepository = configElementRepository;
        _fileSystem = fileSystem;
        _languageCodeService = languageCodeService;
        _logger = logger;
    }

    public Task<MediaStream> SelectVideoStream(MediaVersion version) =>
        version.Streams.First(s => s.MediaStreamKind == MediaStreamKind.Video).AsTask();

    public async Task<Option<MediaStream>> SelectAudioStream(
        MediaItemAudioVersion version,
        StreamingMode streamingMode,
        Channel channel,
        string preferredAudioLanguage,
        string preferredAudioTitle,
        bool shouldLogMessages,
        CancellationToken cancellationToken)
    {
        if (streamingMode == StreamingMode.HttpLiveStreamingDirect &&
            string.IsNullOrWhiteSpace(preferredAudioLanguage) && string.IsNullOrWhiteSpace(preferredAudioTitle))
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug(
                    "Channel {Number} is HLS Direct with no preferred audio language or title; using all audio streams",
                    channel.Number);
            }

            return None;
        }

        string language = (preferredAudioLanguage ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language))
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Channel {Number} has no preferred audio language code", channel.Number);
            }

            Option<string> maybeDefaultLanguage = await _configElementRepository.GetValue<string>(
                ConfigElementKey.FFmpegPreferredLanguageCode,
                cancellationToken);
            maybeDefaultLanguage.Match(
                lang => language = lang.ToLowerInvariant(),
                () =>
                {
                    if (shouldLogMessages)
                    {
                        _logger.LogDebug("FFmpeg has no preferred audio language code; falling back to {Code}", "eng");
                    }

                    language = "eng";
                });
        }

        List<string> allLanguageCodes =
            GetTwoAndThreeLetterLanguageCodes(_languageCodeService.GetAllLanguageCodes([language]));
        if (allLanguageCodes.Count > 1)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Preferred audio language has multiple codes {Codes}", allLanguageCodes);
            }
        }

        try
        {
            switch (version.MediaItem)
            {
                case Episode:
                    var sw = Stopwatch.StartNew();
                    Option<MediaStream> result = await SelectEpisodeAudioStream(
                        channel,
                        allLanguageCodes,
                        version.MediaItem.Id,
                        version.MediaVersion,
                        shouldLogMessages);
                    sw.Stop();
                    if (shouldLogMessages)
                    {
                        _logger.LogDebug("SelectAudioStream duration: {Duration}", sw.Elapsed);
                    }

                    if (result.IsSome)
                    {
                        return result;
                    }

                    break;
                case Movie:
                    var sw2 = Stopwatch.StartNew();
                    Option<MediaStream> result2 = await SelectMovieAudioStream(
                        channel,
                        allLanguageCodes,
                        version.MediaItem.Id,
                        version.MediaVersion,
                        shouldLogMessages);
                    sw2.Stop();
                    if (shouldLogMessages)
                    {
                        _logger.LogDebug("SelectAudioStream duration: {Duration}", sw2.Elapsed);
                    }

                    if (result2.IsSome)
                    {
                        return result2;
                    }

                    break;
                // let default fall through
            }
        }
        catch (Exception ex)
        {
            if (shouldLogMessages)
            {
                _logger.LogError(ex, "Failed to execute audio stream selector script; falling back to built-in logic");
            }
        }

        return DefaultSelectAudioStream(version.MediaVersion, allLanguageCodes, preferredAudioTitle, shouldLogMessages);
    }

    public async Task<Option<Subtitle>> SelectSubtitleStream(
        ImmutableList<Subtitle> subtitles,
        Channel channel,
        string preferredSubtitleLanguage,
        ChannelSubtitleMode subtitleMode,
        bool shouldLogMessages,
        CancellationToken cancellationToken)
    {
        if (channel.MusicVideoCreditsMode is ChannelMusicVideoCreditsMode.GenerateSubtitles &&
            subtitles.FirstOrDefault(s => s.SubtitleKind == SubtitleKind.Generated) is { } generatedSubtitle)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Selecting generated subtitle for channel {Number}", channel.Number);
            }

            return Optional(generatedSubtitle);
        }

        if (subtitleMode == ChannelSubtitleMode.None)
        {
            return None;
        }

        var candidateSubtitles = subtitles.ToList();

        bool useEmbeddedSubtitles = await _configElementRepository
            .GetValue<bool>(ConfigElementKey.FFmpegUseEmbeddedSubtitles, cancellationToken)
            .IfNoneAsync(true);

        if (!useEmbeddedSubtitles)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Ignoring embedded subtitles for channel {Number}", channel.Number);
            }

            candidateSubtitles =
                candidateSubtitles.Filter(s => s.SubtitleKind is not SubtitleKind.Embedded).ToList();
        }

        // next also requires embedded text subtitles to be extracted
        if (channel.StreamingMode is not StreamingMode.HttpLiveStreamingDirect)
        {
            foreach (Subtitle subtitle in candidateSubtitles
                         .Filter(s => s.SubtitleKind is SubtitleKind.Embedded && !s.IsImage)
                         .ToList())
            {
                if (!subtitle.IsExtracted)
                {
                    if (shouldLogMessages)
                    {
                        _logger.LogDebug(
                            "Ignoring embedded subtitle with index {Index} that has not been extracted",
                            subtitle.StreamIndex);
                    }

                    candidateSubtitles.Remove(subtitle);
                }
                else if (string.IsNullOrWhiteSpace(subtitle.Path))
                {
                    if (shouldLogMessages)
                    {
                        _logger.LogDebug(
                            "BUG: ignoring embedded subtitle with index {Index} that is missing a path",
                            subtitle.StreamIndex);
                    }

                    candidateSubtitles.Remove(subtitle);
                }
            }
        }

        var allCodes = new List<string>();
        string language = (preferredSubtitleLanguage ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(language))
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Channel {Number} has no preferred subtitle language code", channel.Number);
            }
        }
        else
        {
            // filter to preferred language
            allCodes = GetTwoAndThreeLetterLanguageCodes(_languageCodeService.GetAllLanguageCodes([language]));
            if (allCodes.Count > 1)
            {
                if (shouldLogMessages)
                {
                    _logger.LogDebug("Preferred subtitle language has multiple codes {Codes}", allCodes);
                }
            }

            candidateSubtitles = candidateSubtitles
                .Filter(s => allCodes.Any(c => string.Equals(s.Language, c, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (candidateSubtitles.Count > 0)
        {
            Option<Subtitle> maybeSelectedSubtitle = subtitleMode switch
            {
                ChannelSubtitleMode.Forced => candidateSubtitles
                    .OrderBy(s => s.StreamIndex)
                    .Find(s => s.Forced)
                    .HeadOrNone(),

                ChannelSubtitleMode.Default => candidateSubtitles
                    .OrderBy(s => s.Default ? 0 : 1)
                    .ThenBy(s => s.StreamIndex)
                    .HeadOrNone(),

                ChannelSubtitleMode.Any => candidateSubtitles
                    .OrderBy(s => s.StreamIndex)
                    .HeadOrNone(),

                _ => Option<Subtitle>.None
            };

            foreach (Subtitle subtitle in maybeSelectedSubtitle)
            {
                if (shouldLogMessages)
                {
                    _logger.LogDebug("Selecting subtitle {@Subtitle}", subtitle);
                }

                return subtitle;
            }
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug(
                "Found no subtitles for channel {ChannelNumber} with mode {Mode} matching language {Language}",
                channel.Number,
                subtitleMode,
                allCodes);
        }

        return None;
    }

    private Option<MediaStream> DefaultSelectAudioStream(
        MediaVersion version,
        IReadOnlyCollection<string> preferredLanguageCodes,
        string preferredAudioTitle,
        bool shouldLogMessages)
    {
        var audioStreams = version.Streams.Filter(s => s.MediaStreamKind == MediaStreamKind.Audio).ToList();

        var correctLanguage = audioStreams.Filter(s =>
                preferredLanguageCodes.Any(c => string.Equals(s.Language, c, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (correctLanguage.Count != 0)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug(
                    "Found {Count} audio streams with preferred audio language code(s) {Code}",
                    correctLanguage.Count,
                    preferredLanguageCodes);
            }

            return PrioritizeAudioTitle(correctLanguage, preferredAudioTitle ?? string.Empty, shouldLogMessages);
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug(
                "Unable to find audio stream with preferred audio language code(s) {Code}",
                preferredLanguageCodes);
        }

        return PrioritizeAudioTitle(audioStreams, preferredAudioTitle ?? string.Empty, shouldLogMessages);
    }

    private Option<MediaStream> PrioritizeAudioTitle(IReadOnlyCollection<MediaStream> streams, string title, bool shouldLogMessages)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return PrioritizeDefault(streams, shouldLogMessages);
        }

        // prioritize matching titles
        var matchingTitle = streams
            .Filter(ms => (ms.Title ?? string.Empty).Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingTitle.Count != 0)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug(
                    "Found {Count} audio streams with preferred title {Title}",
                    matchingTitle.Count,
                    title);
            }

            return PrioritizeDefault(matchingTitle, shouldLogMessages);
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug("Unable to find audio stream with preferred title {Title}", title);
        }

        return PrioritizeDefault(streams, shouldLogMessages);
    }

    private Option<MediaStream> PrioritizeDefault(IReadOnlyCollection<MediaStream> streams, bool shouldLogMessages)
    {
        var sorted = streams.OrderByDescending(s => s.Channels).ToList();
        Option<MediaStream> maybeDefault = Optional(sorted.Find(s => s.Default));
        foreach (MediaStream stream in maybeDefault)
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug("Found audio stream flagged as default");
            }

            return stream;
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug("Unable to find default audio stream; selecting stream with most channels");
        }

        return streams.HeadOrNone();
    }

    private async Task<Option<MediaStream>> SelectEpisodeAudioStream(
        Channel channel,
        List<string> preferredLanguageCodes,
        int episodeId,
        MediaVersion version,
        bool shouldLogMessages)
    {
        string jsScriptPath = Path.ChangeExtension(
            Path.Combine(FileSystemLayout.AudioStreamSelectorScriptsFolder, "episode"),
            "js");

        if (shouldLogMessages)
        {
            _logger.LogDebug("Checking for JS Script at {Path}", jsScriptPath);
        }

        if (!_fileSystem.File.Exists(jsScriptPath))
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug(
                    "Unable to locate episode audio stream selector script; falling back to built-in logic");
            }

            return Option<MediaStream>.None;
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug("Found JS Script at {Path}", jsScriptPath);
        }

        await _scriptEngine.LoadAsync(jsScriptPath);

        EpisodeAudioStreamSelectorData data = await _streamSelectorRepository.GetEpisodeData(episodeId);

        AudioStream[] audioStreams = GetAudioStreamsForScript(version);

        object result = _scriptEngine.Invoke(
            "selectEpisodeAudioStreamIndex",
            channel.Number,
            channel.Name,
            data.ShowTitle,
            data.ShowGuids,
            data.SeasonNumber,
            data.EpisodeNumber,
            data.EpisodeGuids,
            preferredLanguageCodes.ToArray(),
            audioStreams);

        return ProcessScriptResult(version, result, shouldLogMessages);
    }

    private async Task<Option<MediaStream>> SelectMovieAudioStream(
        Channel channel,
        List<string> preferredLanguageCodes,
        int movieId,
        MediaVersion version,
        bool shouldLogMessages)
    {
        string jsScriptPath = Path.ChangeExtension(
            Path.Combine(FileSystemLayout.AudioStreamSelectorScriptsFolder, "movie"),
            "js");

        if (shouldLogMessages)
        {
            _logger.LogDebug("Checking for JS Script at {Path}", jsScriptPath);
        }

        if (!_fileSystem.File.Exists(jsScriptPath))
        {
            if (shouldLogMessages)
            {
                _logger.LogDebug(
                    "Unable to locate movie audio stream selector script; falling back to built-in logic");
            }

            return Option<MediaStream>.None;
        }

        if (shouldLogMessages)
        {
            _logger.LogDebug("Found JS Script at {Path}", jsScriptPath);
        }

        await _scriptEngine.LoadAsync(jsScriptPath);

        MovieAudioStreamSelectorData data = await _streamSelectorRepository.GetMovieData(movieId);

        AudioStream[] audioStreams = GetAudioStreamsForScript(version);

        object result = _scriptEngine.Invoke(
            "selectMovieAudioStreamIndex",
            channel.Number,
            channel.Name,
            data.Title,
            data.Guids,
            preferredLanguageCodes.ToArray(),
            audioStreams);

        return ProcessScriptResult(version, result, shouldLogMessages);
    }

    private Option<MediaStream> ProcessScriptResult(MediaVersion version, object result, bool shouldLogMessages)
    {
        if (result is double d)
        {
            var streamIndex = (int)d;
            Option<MediaStream> maybeStream = version.Streams.Find(s => s.Index == streamIndex);
            foreach (MediaStream stream in maybeStream)
            {
                if (shouldLogMessages)
                {
                    _logger.LogDebug(
                        "JS Script returned audio stream index {Index} with language {Language} and {Channels} audio channel(s)",
                        streamIndex,
                        stream.Language,
                        stream.Channels);
                }

                return stream;
            }

            if (shouldLogMessages)
            {
                _logger.LogWarning(
                    "JS Script returned audio stream index {Index} which does not exist",
                    streamIndex);
            }
        }
        else
        {
            if (shouldLogMessages)
            {
                _logger.LogInformation(
                    "JS Script did not return an audio stream index; falling back to built-in logic");
            }
        }

        return Option<MediaStream>.None;
    }

    private static List<string> GetTwoAndThreeLetterLanguageCodes(List<string> threeLetterLanguageCodes)
    {
        CultureInfo[] allCultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures);
        var result = new System.Collections.Generic.HashSet<string>(threeLetterLanguageCodes);

        foreach (string code in threeLetterLanguageCodes)
        {
            IEnumerable<CultureInfo> cultures = allCultures
                .Filter(ci => string.Equals(ci.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));

            foreach (CultureInfo culture in cultures)
            {
                result.Add(culture.ThreeLetterISOLanguageName);
                result.Add(culture.TwoLetterISOLanguageName);
            }
        }

        return result.ToList();
    }

    private static AudioStream[] GetAudioStreamsForScript(MediaVersion version) => version.Streams
        .Filter(s => s.MediaStreamKind == MediaStreamKind.Audio)
        .Map(a => new AudioStream(a.Index, a.Channels, a.Codec, a.Default, a.Forced, a.Language, a.Title))
        .ToArray();

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private record AudioStream(
        int index,
        int channels,
        string codec,
        bool isDefault,
        bool isForced,
        string language,
        string title);
}
