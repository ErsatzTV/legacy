using System.IO.Abstractions;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Services.RunOnce;

public class CacheCleanerService(
    IServiceScopeFactory serviceScopeFactory,
    SystemStartup systemStartup,
    ILogger<CacheCleanerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await systemStartup.WaitForDatabase(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using IServiceScope scope = serviceScopeFactory.CreateScope();
        await using TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();
        ILocalFileSystem localFileSystem = scope.ServiceProvider.GetRequiredService<ILocalFileSystem>();
        IFileSystem fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();

        if (fileSystem.Directory.Exists(FileSystemLayout.LegacyImageCacheFolder))
        {
            logger.LogInformation("Migrating channel logos from legacy image cache folder");

            List<string> logos = await dbContext.Channels
                .AsNoTracking()
                .SelectMany(c => c.Artwork)
                .Where(a => a.ArtworkKind == ArtworkKind.Logo)
                .Map(a => a.Path)
                .ToListAsync(stoppingToken);

            foreach (string logo in logos)
            {
                string legacyPath = Path.Combine(FileSystemLayout.LegacyImageCacheFolder, logo);
                if (fileSystem.File.Exists(legacyPath))
                {
                    string subfolder = logo[..2];
                    string newPath = Path.Combine(FileSystemLayout.LogoCacheFolder, subfolder, logo);
                    await localFileSystem.CopyFile(legacyPath, newPath);
                }
            }

            logger.LogInformation("Deleting legacy image cache folder");
            Directory.Delete(FileSystemLayout.LegacyImageCacheFolder, true);
        }

        if (fileSystem.Directory.Exists(FileSystemLayout.TranscodeFolder))
        {
            logger.LogInformation("Emptying transcode cache folder");
            localFileSystem.EmptyFolder(FileSystemLayout.TranscodeFolder);
            logger.LogInformation("Done emptying transcode cache folder");
        }

        if (fileSystem.Directory.Exists(FileSystemLayout.ChannelGuideCacheFolder))
        {
            logger.LogInformation("Cleaning channel cache");

            List<string> channelFiles = await dbContext.Channels
                .AsNoTracking()
                .Select(c => c.Number)
                .Map(num => Path.Combine(FileSystemLayout.ChannelGuideCacheFolder, $"{num}.xml"))
                .ToListAsync(stoppingToken);

            foreach (string fileName in localFileSystem.ListFiles(FileSystemLayout.ChannelGuideCacheFolder))
            {
                if (fileName.Contains("channels") || channelFiles.Contains(fileName))
                {
                    continue;
                }

                File.Delete(fileName);
            }
        }

        await StripSubtitleCacheNulBytes(scope, fileSystem, stoppingToken);
    }

    // existing files are not extracted again, so fix them once
    private async Task StripSubtitleCacheNulBytes(
        IServiceScope scope,
        IFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        IConfigElementRepository configElementRepository =
            scope.ServiceProvider.GetRequiredService<IConfigElementRepository>();

        Option<bool> maybeStripped = await configElementRepository.GetValue<bool>(
            ConfigElementKey.FFmpegSubtitleCacheNulStripped,
            cancellationToken);
        if (maybeStripped.IfNone(false))
        {
            return;
        }

        if (fileSystem.Directory.Exists(FileSystemLayout.SubtitleCacheFolder))
        {
            logger.LogInformation("Stripping NUL bytes from subtitle cache");

            var count = 0;
            foreach (string fileName in fileSystem.Directory.EnumerateFiles(
                         FileSystemLayout.SubtitleCacheFolder,
                         "*",
                         SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(fileName);
                if (extension is not (".ass" or ".srt" or ".vtt"))
                {
                    continue;
                }

                if (await SubtitleNulBytes.StripOrDelete(fileSystem, fileName, logger, cancellationToken) is
                    SubtitleNulBytes.StripResult.Stripped)
                {
                    count++;
                }
            }

            logger.LogInformation("Stripped NUL bytes from {Count} cached subtitles", count);
        }

        await configElementRepository.Upsert(ConfigElementKey.FFmpegSubtitleCacheNulStripped, true, cancellationToken);
    }
}
