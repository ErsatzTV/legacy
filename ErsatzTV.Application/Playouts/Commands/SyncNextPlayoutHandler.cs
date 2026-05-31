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
        // gen new folder name
        string versionFolderName = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        string channelFolder = fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, request.ChannelNumber);
        string versionFolder = fileSystem.Path.Combine(channelFolder, versionFolderName);

        logger.LogDebug("versioned playout folder is {Folder}", versionFolder);

        localFileSystem.EnsureFolderExists(versionFolder);

        await WriteAllJsonTo(request.ChannelNumber, versionFolder, cancellationToken);

        string currentFolder = fileSystem.Path.Combine(channelFolder, "current");

        // re-point symlink/junction to new folder
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Directory.Exists(currentFolder))
            {
                var dirInfo = new DirectoryInfo(currentFolder);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    dirInfo.Delete();
                }
                else
                {
                    logger.LogError("Expected junction at {Folder} but found a real directory", currentFolder);
                    return;
                }
            }

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

            fileSystem.File.CreateSymbolicLink(tempLink, versionFolderName);
            _ = Rename(tempLink, currentFolder);
        }

        CleanOldVersions(
            fileSystem.Path.Combine(FileSystemLayout.NextPlayoutsFolder, request.ChannelNumber),
            currentFolder);
    }

    private async Task WriteAllJsonTo(string channelNumber, string targetFolder, CancellationToken cancellationToken)
    {
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

            // get playout deco
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)

            .Include(i => i.Watermarks)

            // get playout templates (and deco templates/decos)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)

            .Include(i => i.MediaItem)
            .ThenInclude(mi => mi.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Episode).EpisodeMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Image).ImageMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as Movie).MovieMetadata)
            .ThenInclude(mm => mm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as OtherVideo).OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(i => (i as RemoteStream).RemoteStreamMetadata)
            .ThenInclude(em => em.Subtitles)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        logger.LogDebug("Located {Count} local playout items", playoutItems.Count);

        // fill in all gaps
        List<PlayoutItem> newPlayoutItems = [];
        Option<DateTimeOffset> lastFinish = Option<DateTimeOffset>.None;
        playoutItems.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        foreach (var playoutItem in playoutItems)
        {
            foreach (var finish in lastFinish)
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
            }

            lastFinish = playoutItem.FinishOffset;
            newPlayoutItems.Add(playoutItem);
        }
        playoutItems = newPlayoutItems;

        Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
            .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
            .BindT(watermarkId => dbContext.ChannelWatermarks
                .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

        Option<Channel> maybeChannelForArtwork = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.Watermark)
            .Include(c => c.Artwork)
            .SingleOrDefaultAsync(c => c.Number == channelNumber, cancellationToken)
            .Map(Optional);

        foreach (IGrouping<DateTime, PlayoutItem> group in playoutItems.GroupBy(pi => pi.StartOffset.Date)
                     .Where(g => g.Any()))
        {
            var first = group.First();
            var last = group.Last();

            string fileName = fileSystem.Path.Combine(
                targetFolder,
                $"{first.StartOffset.ToUnixTimeMilliseconds()}_{last.FinishOffset.ToUnixTimeMilliseconds()}.json");

            var playout = new Core.Next.Playout { Version = "https://ersatztv.org/playout/version/0.0.1", Items = [] };
            foreach (PlayoutItem playoutItem in group)
            {
                Option<Core.Next.PlayoutItem> maybeNextPlayoutItem = await playoutItemConverter.ToNext(
                    maybeChannelForArtwork,
                    maybeGlobalWatermark,
                    playoutOffset,
                    playoutItem,
                    cancellationToken);

                foreach (var nextPlayoutItem in maybeNextPlayoutItem)
                {
                    playout.Items.Add(nextPlayoutItem);
                }
            }

            await fileSystem.File.WriteAllTextAsync(fileName, Core.Next.Serialize.ToJson(playout), cancellationToken);
        }
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
