using System.IO.Abstractions;
using System.IO.Compression;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Troubleshooting;

public class ArchiveMediaSampleHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    IFileSystem fileSystem,
    ILogger<ArchiveMediaSampleHandler> logger)
    : TroubleshootingHandlerBase(
        plexPathReplacementService,
        jellyfinPathReplacementService,
        embyPathReplacementService,
        fileSystem), IRequestHandler<ArchiveMediaSample, Option<string>>
{
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<Option<string>> Handle(ArchiveMediaSample request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Tuple<MediaItem, string>> validation = await Validate(
            dbContext,
            request,
            cancellationToken);

        foreach ((MediaItem mediaItem, string ffmpegPath) in validation.SuccessToSeq())
        {
            Option<string> maybeMediaSample = await GetMediaSample(
                request,
                dbContext,
                mediaItem,
                ffmpegPath,
                cancellationToken);

            foreach (string mediaSample in maybeMediaSample)
            {
                return await GetArchive(request, mediaSample, cancellationToken);
            }
        }

        return Option<string>.None;
    }

    private async Task<Option<string>> GetArchive(
        ArchiveMediaSample request,
        string mediaSample,
        CancellationToken cancellationToken)
    {
        string tempFile = Path.GetTempFileName();

        try
        {
            await using ZipArchive zipArchive = await ZipFile.OpenAsync(
                tempFile,
                ZipArchiveMode.Update,
                cancellationToken);

            string fileName = Path.GetFileName(mediaSample);
            await zipArchive.CreateEntryFromFileAsync(mediaSample, fileName, cancellationToken);

            return tempFile;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to archive media sample for media item {MediaItemId}", request.MediaItemId);
            _fileSystem.File.Delete(tempFile);
        }

        return Option<string>.None;
    }

    private async Task<Option<string>> GetMediaSample(
        ArchiveMediaSample request,
        TvContext dbContext,
        MediaItem mediaItem,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        try
        {
            string mediaItemPath = await GetMediaItemPath(dbContext, mediaItem, cancellationToken);
            if (string.IsNullOrEmpty(mediaItemPath))
            {
                logger.LogWarning(
                    "Media item {MediaItemId} does not exist on disk; cannot extract media sample.",
                    mediaItem.Id);

                return Option<string>.None;
            }

            string extension = Path.GetExtension(mediaItemPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                // this can help with remote servers (e.g. mediaItemPath is http://localhost/whatever)
                extension = Path.GetExtension(await GetLocalPath(mediaItem, cancellationToken));

                if (string.IsNullOrWhiteSpace(extension))
                {
                    // fall back to mkv when extension is otherwise unknown
                    extension = "mkv";
                }
            }

            bool isRemoteUri = Uri.TryCreate(mediaItemPath, UriKind.Absolute, out Uri uriResult)
                               && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
            if (isRemoteUri && extension.Contains('?'))
            {
                extension = extension.Split('?')[0];
            }

            string tempPath = Path.GetTempPath();
            string fileName = Path.ChangeExtension(Guid.NewGuid().ToString(), extension);
            string outputPath = Path.Combine(tempPath, fileName);

            List<string> arguments =
            [
                "-nostdin",
                "-i", mediaItemPath,
                "-t", "30",
                "-map", "0",
                "-c", "copy",
                outputPath
            ];

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linkedTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

            logger.LogDebug("media sample arguments {Arguments}", arguments);

            BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
                .WithArguments(arguments)
                .WithWorkingDirectory(FileSystemLayout.FontsCacheFolder)
                .WithStandardErrorPipe(PipeTarget.Null)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(linkedTokenSource.Token);

            if (result.IsSuccess)
            {
                return outputPath;
            }

            logger.LogWarning(
                "Failed to extract media sample for media item {MediaItemId} - exit code {ExitCode}",
                request.MediaItemId,
                result.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract media sample for media item {MediaItemId}", request.MediaItemId);
        }

        return Option<string>.None;
    }

    private static async Task<Validation<BaseError, Tuple<MediaItem, string>>> Validate(
        TvContext dbContext,
        ArchiveMediaSample request,
        CancellationToken cancellationToken) =>
        (await MediaItemMustExist(dbContext, request.MediaItemId, cancellationToken),
            await FFmpegPathMustExist(dbContext, cancellationToken))
        .Apply((mediaItem, ffmpegPath) => Tuple(mediaItem, ffmpegPath));
}
