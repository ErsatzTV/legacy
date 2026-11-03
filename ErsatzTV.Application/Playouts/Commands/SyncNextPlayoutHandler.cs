using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CommandResult = CliWrap.CommandResult;
using PlayoutItem = ErsatzTV.Core.Domain.PlayoutItem;

namespace ErsatzTV.Application.Playouts;

public partial class SyncNextPlayoutHandler(
    IPlayoutItemConverter playoutItemConverter,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<SyncNextPlayoutHandler> logger)
    : IRequestHandler<SyncNextPlayout>
{
    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static partial int Rename(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string newpath
    );

    public async Task Handle(SyncNextPlayout request, CancellationToken cancellationToken)
    {
        string channelFolder = fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, request.ChannelNumber);
        string currentFolder = fileSystem.Path.Combine(channelFolder, "current");

        Dictionary<string, string> playoutFiles = await BuildPlayoutFiles(request.ChannelNumber, cancellationToken);

        if (await TryUpdateCurrentFolder(request.ChannelNumber, currentFolder, playoutFiles, cancellationToken))
        {
            return;
        }

        // gen new folder name
        string versionFolderName = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string versionFolder = fileSystem.Path.Combine(channelFolder, versionFolderName);

        logger.LogDebug("versioned playout folder is {Folder}", versionFolder);

        localFileSystem.EnsureFolderExists(versionFolder);

        foreach ((string fileName, string json) in playoutFiles)
        {
            await fileSystem.File.WriteAllTextAsync(
                fileSystem.Path.Combine(versionFolder, fileName),
                json,
                cancellationToken);
        }

        try
        {
            if (!TryRemoveCurrentFolder(currentFolder))
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var stdErrBuffer = new StringBuilder();
                CommandResult command = await Cli.Wrap("cmd.exe")
                    .WithArguments(["/c", "mklink", "/j", "current", versionFolderName])
                    .WithWorkingDirectory(channelFolder)
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);

                if (!command.IsSuccess)
                {
                    logger.LogError("Failed to link current playout JSON folder: {Error}", stdErrBuffer);
                }
            }
            else
            {
                string tempLink = fileSystem.Path.Combine(
                    FileSystemLayout.NextPlayoutsFolder,
                    request.ChannelNumber,
                    fileSystem.Path.GetRandomFileName());

                try
                {
                    fileSystem.File.CreateSymbolicLink(tempLink, versionFolderName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(
                        ex,
                        "Failed to link current playout JSON folder; the config folder must support symlinks");
                    return;
                }

                if (Rename(tempLink, currentFolder) != 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    logger.LogError(
                        "Failed to link current playout JSON folder: {Error}",
                        Marshal.GetPInvokeErrorMessage(errno));
                    fileSystem.File.Delete(tempLink);
                }
            }
        }
        finally
        {
            // each sync adds a version folder; clean up even if linking fails
            CleanOldVersions(channelFolder, currentFolder);
        }
    }

    private bool TryRemoveCurrentFolder(string currentFolder)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (!Directory.Exists(currentFolder))
        {
            // mklink fails on an existing file; unix renames over it
            if (isWindows && File.Exists(currentFolder))
            {
                logger.LogWarning(
                    "Expected junction at {Folder} but found a file; replacing it",
                    currentFolder);

                try
                {
                    File.Delete(currentFolder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Failed to remove file at {Folder}", currentFolder);
                    return false;
                }
            }

            return true;
        }

        var dirInfo = new DirectoryInfo(currentFolder);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // unix atomically renames the new symlink over the old one
            if (isWindows)
            {
                dirInfo.Delete();
            }

            return true;
        }

        // copying app data (explorer, backups, cp -L) turns links into real folders
        logger.LogWarning(
            "Expected {LinkKind} at {Folder} but found a real directory; replacing it",
            isWindows ? "junction" : "symlink",
            currentFolder);

        try
        {
            dirInfo.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to remove real directory at {Folder}", currentFolder);
            return false;
        }
    }

    private async Task<bool> TryUpdateCurrentFolder(
        string channelNumber,
        string currentFolder,
        Dictionary<string, string> playoutFiles,
        CancellationToken cancellationToken)
    {
        var currentInfo = new DirectoryInfo(currentFolder);
        if (!currentInfo.Exists || !currentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset cutoff = NextPlayoutFolderPlanner.Cutoff(now);

        List<NextPlayoutFileState> existing = [];
        foreach (string path in fileSystem.Directory.EnumerateFiles(currentFolder, "*.json"))
        {
            string json = await fileSystem.File.ReadAllTextAsync(path, cancellationToken);
            Option<NextPlayoutFileState> maybeState =
                NextPlayoutFileState.FromJson(fileSystem.Path.GetFileName(path), json, cutoff);
            if (maybeState.IsNone)
            {
                return false;
            }

            existing.AddRange(maybeState);
        }

        List<NextPlayoutFileState> desired = [];
        foreach ((string fileName, string json) in playoutFiles)
        {
            Option<NextPlayoutFileState> maybeState = NextPlayoutFileState.FromJson(fileName, json, cutoff);
            if (maybeState.IsNone)
            {
                return false;
            }

            desired.AddRange(maybeState);
        }

        NextPlayoutFolderPlan plan = NextPlayoutFolderPlanner.Plan(existing, desired, now);
        switch (plan.Kind)
        {
            case NextPlayoutFolderPlanKind.NoChange:
                logger.LogDebug("Playout for channel {Channel} is unchanged", channelNumber);
                return true;
            case NextPlayoutFolderPlanKind.FullSwap:
                return false;
        }

        try
        {
            // write before delete; temp names must not parse as playout windows
            foreach (string fileName in plan.FilesToWrite)
            {
                string tempFile = fileSystem.Path.Combine(currentFolder, $".sync-{fileSystem.Path.GetRandomFileName()}");
                try
                {
                    await fileSystem.File.WriteAllTextAsync(tempFile, playoutFiles[fileName], cancellationToken);
                    fileSystem.File.Move(tempFile, fileSystem.Path.Combine(currentFolder, fileName), overwrite: true);
                }
                finally
                {
                    if (fileSystem.File.Exists(tempFile))
                    {
                        fileSystem.File.Delete(tempFile);
                    }
                }
            }

            foreach (string fileName in plan.FilesToDelete)
            {
                fileSystem.File.Delete(fileSystem.Path.Combine(currentFolder, fileName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to update playout for channel {Channel} in place", channelNumber);
            return false;
        }

        logger.LogDebug(
            "Updated playout for channel {Channel} in place: wrote {Written}, deleted {Deleted}",
            channelNumber,
            plan.FilesToWrite.Count,
            plan.FilesToDelete.Count);

        return true;
    }

    private async Task<Dictionary<string, string>> BuildPlayoutFiles(
        string channelNumber,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> result = [];

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        TimeSpan playoutOffset = TimeSpan.Zero;
        string mirrorChannelNumber = null;
        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.MirrorSourceChannel)
            .Filter(c => c.PlayoutSource == ChannelPlayoutSource.Mirror && c.MirrorSourceChannelId != null)
            .SelectOneAsync(
                c => c.Number == channelNumber,
                c => c.Number == channelNumber,
                cancellationToken);
        foreach (Channel channel in maybeChannel)
        {
            mirrorChannelNumber = channel.MirrorSourceChannel.Number;
            playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;
        }

        List<PlayoutItem> playoutItems = await dbContext.PlayoutItems
            .AsNoTracking()
            .Where(i => i.Playout.Channel.Number == (mirrorChannelNumber ?? channelNumber))
            .IncludeForNextPlayout()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        logger.LogDebug("Located {Count} local playout items", playoutItems.Count);

        // fill in all gaps
        playoutItems.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        if (playoutItems.Count > 0)
        {
            List<PlayoutItem> newPlayoutItems = [];

            // utc midnight, not now, keeps the leading gap stable between syncs
            var finish = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            foreach (var playoutItem in playoutItems)
            {
                if (finish < playoutItem.StartOffset)
                {
                    newPlayoutItems.Add(
                        new DynamicPlayoutItem
                        {
                            Start = finish.UtcDateTime,
                            Finish = playoutItem.Start
                        });
                }

                finish = playoutItem.FinishOffset;
                newPlayoutItems.Add(playoutItem);
            }

            playoutItems = newPlayoutItems;
        }

        Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
            .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
            .BindT(watermarkId => dbContext.ChannelWatermarks
                .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

        Option<Channel> maybeChannelForArtwork = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.Watermark)
            .Include(c => c.Artwork)
            .Include(c => c.FFmpegProfile)
            .ThenInclude(ff => ff.Resolution)
            .SingleOrDefaultAsync(c => c.Number == channelNumber, cancellationToken)
            .Map(Optional);

        foreach (IGrouping<DateTime, PlayoutItem> group in playoutItems.GroupBy(pi => pi.StartOffset.Date)
                     .Where(g => g.Any()))
        {
            var first = group.First();
            var last = group.Last();

            // ToNext shifts mirror items; the file window must match
            string fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"{(first.StartOffset + playoutOffset).ToUnixTimeMilliseconds()}_{(last.FinishOffset + playoutOffset).ToUnixTimeMilliseconds()}.json");

            var playout = new Core.Next.Playout { Version = PlayoutItemConverter.PlayoutVersion, Items = [] };
            foreach (PlayoutItem playoutItem in group)
            {
                Option<Core.Next.PlayoutItem> maybeNextPlayoutItem = await playoutItemConverter.ToNext(
                    maybeChannelForArtwork,
                    maybeGlobalWatermark,
                    playoutOffset,
                    playoutItem,
                    Option<List<Subtitle>>.None,
                    shouldLogMessages: false,
                    cancellationToken);

                foreach (var nextPlayoutItem in maybeNextPlayoutItem)
                {
                    playout.Items.Add(nextPlayoutItem);
                }
            }

            result[fileName] = Core.Next.Serialize.ToJson(playout);
        }

        return result;
    }

    private void CleanOldVersions(
        string playoutRoot,
        string currentLinkPath,
        int keepVersions = 2,
        TimeSpan? gracePeriod = null)
    {
        gracePeriod ??= TimeSpan.FromMinutes(5);

        string currentResolvedPath = null;
        if (Directory.Exists(currentLinkPath))
        {
            currentResolvedPath = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(currentLinkPath) ?? "",
                    Directory.ResolveLinkTarget(currentLinkPath, true)?.FullName ?? ""
                ));
        }

        var directories = Directory.GetDirectories(playoutRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => long.TryParse(d.Name, out _))
            .OrderByDescending(d => d.Name)
            .ToList();

        int keptCount = 0;

        foreach (var dir in directories)
        {
            string fullDir = dir.FullName;

            if (fullDir.Equals(currentResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                keptCount++;
                continue;
            }

            if (keptCount < keepVersions)
            {
                keptCount++;
                continue;
            }

            if (DateTime.Now - dir.LastWriteTime < gracePeriod)
            {
                continue;
            }

            try
            {
                dir.Delete(recursive: true);
                logger.LogDebug("Cleaned up old playout version: {Folder}", dir.Name);
            }
            catch (IOException)
            {
                // ignore errors; will be cleaned up next time through
                logger.LogDebug("Skipping busy folder: {Folder}", dir.Name);
            }
        }
    }
}
