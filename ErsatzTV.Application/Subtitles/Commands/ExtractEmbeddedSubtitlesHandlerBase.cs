using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Builders;
using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Subtitles;

[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms")]
public abstract class ExtractEmbeddedSubtitlesHandlerBase(IFileSystem fileSystem, ILogger logger)
{
    protected static Task<Validation<BaseError, string>> FFmpegPathMustExist(
        TvContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken)
            .FilterT(File.Exists)
            .Map(maybePath => maybePath.ToValidation<BaseError>("FFmpeg path does not exist on filesystem"));

    protected static async Task<List<int>> GetMediaItemIdsWithTextSubtitles(
        TvContext dbContext,
        List<int> mediaItemIds,
        CancellationToken cancellationToken)
    {
        var result = new List<int>();

        try
        {
            List<int> episodeIds = await dbContext.EpisodeMetadata
                .AsNoTracking()
                .Filter(em => mediaItemIds.Contains(em.EpisodeId))
                .Filter(em => em.Subtitles.Any(s => s.SubtitleKind == SubtitleKind.Embedded &&
                                                    s.Codec != "hdmv_pgs_subtitle" && s.Codec != "dvd_subtitle" &&
                                                    s.Codec != "dvdsub" &&
                                                    s.Codec != "vobsub" && s.Codec != "pgssub" && s.Codec != "pgs"))
                .Map(em => em.EpisodeId)
                .ToListAsync(cancellationToken);
            result.AddRange(episodeIds);

            List<int> movieIds = await dbContext.MovieMetadata
                .AsNoTracking()
                .Filter(mm => mediaItemIds.Contains(mm.MovieId))
                .Filter(mm => mm.Subtitles.Any(s => s.SubtitleKind == SubtitleKind.Embedded &&
                                                    s.Codec != "hdmv_pgs_subtitle" && s.Codec != "dvd_subtitle" &&
                                                    s.Codec != "dvdsub" &&
                                                    s.Codec != "vobsub" && s.Codec != "pgssub" && s.Codec != "pgs"))
                .Map(mm => mm.MovieId)
                .ToListAsync(cancellationToken);
            result.AddRange(movieIds);

            List<int> musicVideoIds = await dbContext.MusicVideoMetadata
                .AsNoTracking()
                .Filter(mm => mediaItemIds.Contains(mm.MusicVideoId))
                .Filter(mm => mm.Subtitles.Any(s => s.SubtitleKind == SubtitleKind.Embedded &&
                                                    s.Codec != "hdmv_pgs_subtitle" && s.Codec != "dvd_subtitle" &&
                                                    s.Codec != "dvdsub" &&
                                                    s.Codec != "vobsub" && s.Codec != "pgssub" && s.Codec != "pgs"))
                .Map(mm => mm.MusicVideoId)
                .ToListAsync(cancellationToken);
            result.AddRange(musicVideoIds);

            List<int> otherVideoIds = await dbContext.OtherVideoMetadata
                .AsNoTracking()
                .Filter(ovm => mediaItemIds.Contains(ovm.OtherVideoId))
                .Filter(ovm => ovm.Subtitles.Any(s => s.SubtitleKind == SubtitleKind.Embedded &&
                                                      s.Codec != "hdmv_pgs_subtitle" && s.Codec != "dvd_subtitle" &&
                                                      s.Codec != "dvdsub" &&
                                                      s.Codec != "vobsub" && s.Codec != "pgssub" && s.Codec != "pgs"))
                .Map(ovm => ovm.OtherVideoId)
                .ToListAsync(cancellationToken);
            result.AddRange(otherVideoIds);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // do nothing
        }

        return result;
    }

    protected async Task<bool> ExtractSubtitles(
        TvContext dbContext,
        int mediaItemId,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        bool extractedAnything = false;

        foreach (MediaItem mediaItem in await GetMediaItem(dbContext, mediaItemId, cancellationToken))
        {
            foreach (List<Subtitle> allSubtitles in GetSubtitles(mediaItem))
            {
                var sw = Stopwatch.StartNew();

                var subtitlesToExtract = new List<SubtitleToExtract>();

                // find each subtitle that needs extraction
                IEnumerable<Subtitle> subtitles = allSubtitles
                    .Filter(s => s.SubtitleKind == SubtitleKind.Embedded)
                    .Filter(s => s.Codec != "hdmv_pgs_subtitle" && s.Codec != "dvd_subtitle" && s.Codec != "dvdsub" &&
                                 s.Codec != "vobsub" && s.Codec != "pgssub" && s.Codec != "pgs")
                    .Filter(s => !s.IsExtracted || string.IsNullOrWhiteSpace(s.Path) ||
                                 FileDoesntExist(mediaItem.Id, s));

                // find cache paths for each subtitle
                foreach (Subtitle subtitle in subtitles)
                {
                    Option<string> maybePath = GetRelativeOutputPath(mediaItem.Id, subtitle);
                    foreach (string path in maybePath)
                    {
                        subtitlesToExtract.Add(new SubtitleToExtract(subtitle, path));
                    }
                }

                if (subtitlesToExtract.Count == 0)
                {
                    continue;
                }

                string mediaItemPath = await GetMediaItemPath(dbContext, mediaItem);

                ArgumentsBuilder args = new ArgumentsBuilder()
                    .Add("-nostdin")
                    .Add("-hide_banner")
                    .Add("-i").Add(mediaItemPath);

                var subtitleIndexes = allSubtitles
                    .Filter(s => s.SubtitleKind is SubtitleKind.Embedded)
                    .OrderBy(s => s.StreamIndex)
                    .Select(s => s.StreamIndex)
                    .ToList();

                foreach (SubtitleToExtract subtitle in subtitlesToExtract)
                {
                    string fullOutputPath = Path.Combine(FileSystemLayout.SubtitleCacheFolder, subtitle.OutputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath));
                    if (fileSystem.File.Exists(fullOutputPath))
                    {
                        File.Delete(fullOutputPath);
                    }

                    args.Add("-map").Add($"0:s:{subtitleIndexes.IndexOf(subtitle.Subtitle.StreamIndex)}")
                        .Add("-c:s").Add(subtitle.Subtitle.Codec == "mov_text" ? "text" : "copy")
                        .Add(fullOutputPath);
                }

                BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
                    .WithArguments(args.Build())
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);

                sw.Stop();

                if (result.ExitCode == 0)
                {
                    foreach (SubtitleToExtract subtitle in subtitlesToExtract)
                    {
                        await dbContext.Connection.ExecuteAsync(
                            "UPDATE `Subtitle` SET `IsExtracted` = 1, `Path` = @Path WHERE `Id` = @SubtitleId",
                            new { SubtitleId = subtitle.Subtitle.Id, Path = subtitle.OutputPath });
                    }

                    logger.LogDebug(
                        "Successfully extracted {Count} subtitles in {Duration}",
                        subtitlesToExtract.Count,
                        sw.Elapsed.Humanize());

                    extractedAnything = true;
                }
                else
                {
                    logger.LogError(
                        "Failed to extract subtitles in {Duration}. {Error}",
                        sw.Elapsed.Humanize(),
                        result.StandardError);
                }
            }
        }

        return extractedAnything;
    }

    protected async Task<bool> ExtractFonts(
        TvContext dbContext,
        int mediaItemId,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        bool extractedAnything = false;

        foreach (MediaItem mediaItem in await GetMediaItem(dbContext, mediaItemId, cancellationToken))
        {
            MediaVersion headVersion = mediaItem.GetHeadVersion();
            var attachments = headVersion.Streams
                .Filter(s => s.MediaStreamKind == MediaStreamKind.Attachment)
                .OrderBy(s => s.Index)
                .ToList();

            for (var attachmentIndex = 0; attachmentIndex < attachments.Count; attachmentIndex++)
            {
                MediaStream fontStream = attachments[attachmentIndex];

                if (!(fontStream.MimeType ?? string.Empty).Contains("font") &&
                    !(fontStream.MimeType ?? string.Empty).Contains("opentype"))
                {
                    // not a font
                    continue;
                }

                string fullOutputPath = Path.Combine(FileSystemLayout.FontsCacheFolder, fontStream.FileName);
                if (fileSystem.File.Exists(fullOutputPath))
                {
                    // already extracted
                    continue;
                }

                string mediaItemPath = await GetMediaItemPath(dbContext, mediaItem);

                var arguments =
                    $"-nostdin -hide_banner -dump_attachment:t:{attachmentIndex} \"\" -i \"{mediaItemPath}\" -y";

                BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
                    .WithWorkingDirectory(FileSystemLayout.FontsCacheFolder)
                    .WithArguments(arguments)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);

                // ffmpeg seems to return exit code 1 in all cases when dumping an attachment
                // so ignore it and check success a different way
                if (fileSystem.File.Exists(fullOutputPath))
                {
                    logger.LogDebug("Successfully extracted font {Font}", fontStream.FileName);
                    extractedAnything = true;
                }
                else
                {
                    logger.LogError(
                        "Failed to extract attached font {Font}. {Error}",
                        fontStream.FileName,
                        result.StandardError);
                }
            }
        }

        return extractedAnything;
    }


    private static async Task<Option<MediaItem>> GetMediaItem(
        TvContext dbContext,
        int mediaItemId,
        CancellationToken cancellationToken) =>
        await dbContext.MediaItems
            .AsNoTracking()
            .Include(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as Episode).EpisodeMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as Movie).MovieMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as OtherVideo).OtherVideoMetadata)
            .ThenInclude(em => em.Subtitles)
            .SelectOneAsync(e => e.Id, e => e.Id == mediaItemId, cancellationToken);

    private static async Task<string> GetMediaItemPath(TvContext dbContext, MediaItem mediaItem)
    {
        MediaVersion version = mediaItem.GetHeadVersion();

        MediaFile file = version.MediaFiles.Head();
        switch (file)
        {
            case PlexMediaFile pmf:
                Option<int> maybeId = await dbContext.Connection.QuerySingleOrDefaultAsync<int>(
                        @"SELECT PMS.Id FROM PlexMediaSource PMS
                  INNER JOIN Library L on PMS.Id = L.MediaSourceId
                  INNER JOIN LibraryPath LP on L.Id = LP.LibraryId
                  WHERE LP.Id = @LibraryPathId",
                        new { mediaItem.LibraryPathId })
                    .Map(Optional);

                foreach (int plexMediaSourceId in maybeId)
                {
                    return $"http://localhost:{Settings.StreamingPort}/media/plex/{plexMediaSourceId}/{pmf.Key}";
                }

                break;
        }

        return mediaItem switch
        {
            JellyfinMovie jellyfinMovie =>
                $"http://localhost:{Settings.StreamingPort}/media/jellyfin/{jellyfinMovie.ItemId}",
            JellyfinEpisode jellyfinEpisode =>
                $"http://localhost:{Settings.StreamingPort}/media/jellyfin/{jellyfinEpisode.ItemId}",
            EmbyMovie embyMovie => $"http://localhost:{Settings.StreamingPort}/media/emby/{embyMovie.ItemId}",
            EmbyEpisode embyEpisode => $"http://localhost:{Settings.StreamingPort}/media/emby/{embyEpisode.ItemId}",
            _ => file.Path
        };
    }

    private bool FileDoesntExist(int mediaItemId, Subtitle subtitle)
    {
        foreach (string path in GetRelativeOutputPath(mediaItemId, subtitle))
        {
            return !fileSystem.File.Exists(Path.Combine(FileSystemLayout.SubtitleCacheFolder, path));
        }

        return false;
    }

    private static Option<List<Subtitle>> GetSubtitles(MediaItem mediaItem) =>
        mediaItem switch
        {
            Episode e => e.EpisodeMetadata.Head().Subtitles,
            Movie m => m.MovieMetadata.Head().Subtitles,
            MusicVideo mv => mv.MusicVideoMetadata.Head().Subtitles,
            OtherVideo ov => ov.OtherVideoMetadata.Head().Subtitles,
            _ => None
        };

    private static Option<string> GetRelativeOutputPath(int mediaItemId, Subtitle subtitle)
    {
        string name = GetStringHash($"{mediaItemId}_{subtitle.StreamIndex}_{subtitle.Codec}");
        string subfolder = name[..2];
        string subfolder2 = name[2..4];

        string nameWithExtension = subtitle.Codec switch
        {
            "subrip" or "srt" => $"{name}.srt",
            "ass" => $"{name}.ass",
            "webvtt" => $"{name}.vtt",
            "mov_text" => $"{name}.srt",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(nameWithExtension))
        {
            return None;
        }

        return Path.Combine(subfolder, subfolder2, nameWithExtension);
    }

    private static string GetStringHash(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        byte[] textData = Encoding.UTF8.GetBytes(text);
        byte[] hash = MD5.HashData(textData);
        return Convert.ToHexString(hash);
    }

    private sealed record SubtitleToExtract(Subtitle Subtitle, string OutputPath);
}
