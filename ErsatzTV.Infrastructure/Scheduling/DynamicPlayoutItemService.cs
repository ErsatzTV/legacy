using System.IO.Abstractions;
using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Scheduling;

public class DynamicPlayoutItemService(
    IFileSystem fileSystem,
    IMediaCollectionRepository mediaCollectionRepository,
    ITelevisionRepository televisionRepository,
    IArtistRepository artistRepository,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    IDecoSelector decoSelector) : IDynamicPlayoutItemService
{
    private static readonly Random FallbackRandom = new();

#pragma warning disable CA1805
#if DEBUG_NO_SYNC
    private static readonly bool IsDebugNoSync = true;
#else
    private static readonly bool IsDebugNoSync = false;
#endif
#pragma warning restore CA1805

    public async Task<Either<BaseError, PlayoutItemWithPath>> CheckForFallbackFiller(
        TvContext dbContext,
        Channel channel,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Either<BaseError, PlayoutItemWithPath> result = new UnableToLocatePlayoutItem();

        Option<Playout> maybePlayout = await dbContext.Playouts
            .AsNoTracking()

            // get playout deco
            .Include(p => p.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(p => p.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)

            // get playout templates (and deco templates/decos)
            .Include(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .SelectOneAsync(
                p => p.ChannelId,
                p => p.ChannelId == (channel.MirrorSourceChannelId ?? channel.Id),
                cancellationToken);

        foreach (Playout playout in maybePlayout)
        {
            result = await CheckForFallbackFiller(
                dbContext,
                channel,
                playout,
                now,
                cancellationToken);
        }

        if (maybePlayout.IsNone)
        {
            result = await CheckForFallbackFiller(
                dbContext,
                channel,
                null,
                now,
                cancellationToken);
        }

        return result;
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> CheckForFallbackFiller(
        TvContext dbContext,
        Channel channel,
        Playout playout,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Option<FillerPreset> maybeFallback = Option<FillerPreset>.None;

        DeadAirFallbackResult decoDeadAirFallback = GetDecoDeadAirFallback(playout, now);
        switch (decoDeadAirFallback)
        {
            case CustomDeadAirFallback custom:
                maybeFallback = new FillerPreset
                {
                    // always allow watermarks here
                    // deco settings will disable watermarks if appropriate
                    AllowWatermarks = true,

                    CollectionType = custom.CollectionType,
                    CollectionId = custom.CollectionId,
                    MediaItemId = custom.MediaItemId,
                    MultiCollectionId = custom.MultiCollectionId,
                    SmartCollectionId = custom.SmartCollectionId
                };
                break;
            case DisableDeadAirFallback:
                // do nothing
                break;
            case InheritDeadAirFallback:
                // check for channel fallback
                maybeFallback = await dbContext.FillerPresets
                    .SelectOneAsync(w => w.Id, w => w.Id == channel.FallbackFillerId, cancellationToken);

                // then check for global fallback
                if (maybeFallback.IsNone)
                {
                    maybeFallback = await dbContext.ConfigElements
                        .GetValue<int>(ConfigElementKey.FFmpegGlobalFallbackFillerId, cancellationToken)
                        .BindT(fillerId => dbContext.FillerPresets.SelectOneAsync(
                            w => w.Id,
                            w => w.Id == fillerId,
                            cancellationToken));
                }

                break;
        }


        foreach (FillerPreset fallbackPreset in maybeFallback)
        {
            // turn this into a playout item

            var collectionKey = CollectionKey.ForFillerPreset(fallbackPreset);
            List<MediaItem> items = await MediaItemsForCollection.Collect(
                mediaCollectionRepository,
                televisionRepository,
                artistRepository,
                collectionKey,
                cancellationToken);

            // ignore the fallback filler preset if it has no items
            if (items.Count == 0)
            {
                break;
            }

            // get a random item
            MediaItem item = items[FallbackRandom.Next(items.Count)];

            Option<TimeSpan> maybeDuration = await dbContext.PlayoutItems
                .Filter(pi => pi.Playout.ChannelId == (channel.MirrorSourceChannelId ?? channel.Id))
                .Filter(pi => pi.Start > now.UtcDateTime)
                .OrderBy(pi => pi.Start)
                .FirstOrDefaultAsync(cancellationToken)
                .Map(Optional)
                .MapT(pi => pi.StartOffset - now);

            MediaVersion version = item.GetHeadVersion();

            version.MediaFiles = await dbContext.MediaFiles
                .AsNoTracking()
                .Filter(mf => mf.MediaVersionId == version.Id)
                .ToListAsync(cancellationToken);

            version.Streams = await dbContext.MediaStreams
                .AsNoTracking()
                .Filter(ms => ms.MediaVersionId == version.Id)
                .ToListAsync(cancellationToken);

            // always play min(duration to next item, version.Duration)
            TimeSpan duration = await maybeDuration.IfNoneAsync(version.Duration);
            if (version.Duration < duration)
            {
                duration = version.Duration;
            }

            DateTimeOffset finish = now.Add(duration);

            var playoutItem = new PlayoutItem
            {
                MediaItem = item,
                MediaItemId = item.Id,
                Start = now.UtcDateTime,
                Finish = finish.UtcDateTime,
                FillerKind = FillerKind.Fallback,
                InPoint = TimeSpan.Zero,
                OutPoint = duration,
                DisableWatermarks = !fallbackPreset.AllowWatermarks,
                Watermarks = [],
                PlayoutItemWatermarks = [],
                GraphicsElements = [],
                PlayoutItemGraphicsElements = []
            };

            return await ValidatePlayoutItemPath(dbContext, playoutItem, cancellationToken);
        }

        return new UnableToLocatePlayoutItem();
    }

    public async Task<Either<BaseError, PlayoutItemWithPath>> ValidatePlayoutItemPath(
        TvContext dbContext,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken)
    {
        string path = await playoutItem.MediaItem.GetLocalPath(
            plexPathReplacementService,
            jellyfinPathReplacementService,
            embyPathReplacementService,
            cancellationToken);

        if (IsDebugNoSync)
        {
            // pretend it exists so we get a nice error message
            return new PlayoutItemWithPath(playoutItem, path);
        }

        // check filesystem first
        if (fileSystem.File.Exists(path))
        {
            if (playoutItem.MediaItem is RemoteStream remoteStream)
            {
                path = !string.IsNullOrWhiteSpace(remoteStream.Url)
                    ? remoteStream.Url
                    : $"http://localhost:{Settings.StreamingPort}/ffmpeg/remote-stream/{remoteStream.Id}";
            }

            return new PlayoutItemWithPath(playoutItem, path);
        }

        // attempt to remotely stream plex
        MediaFile file = playoutItem.MediaItem.GetHeadVersion().MediaFiles.Head();
        switch (file)
        {
            case PlexMediaFile pmf:
                Option<int> maybeId = await dbContext.Connection.QuerySingleOrDefaultAsync<int>(
                        @"SELECT PMS.Id FROM PlexMediaSource PMS
                  INNER JOIN Library L on PMS.Id = L.MediaSourceId
                  INNER JOIN LibraryPath LP on L.Id = LP.LibraryId
                  WHERE LP.Id = @LibraryPathId",
                        new { playoutItem.MediaItem.LibraryPathId })
                    .Map(Optional);

                foreach (int plexMediaSourceId in maybeId)
                {
                    return new PlayoutItemWithPath(
                        playoutItem,
                        $"http://localhost:{Settings.StreamingPort}/media/plex/{plexMediaSourceId}/{pmf.Key}");
                }

                break;
        }

        // attempt to remotely stream jellyfin
        Option<string> jellyfinItemId = playoutItem.MediaItem switch
        {
            JellyfinEpisode e => e.ItemId,
            JellyfinMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in jellyfinItemId)
        {
            return new PlayoutItemWithPath(
                playoutItem,
                $"http://localhost:{Settings.StreamingPort}/media/jellyfin/{itemId}");
        }

        // attempt to remotely stream emby
        Option<string> embyItemId = playoutItem.MediaItem switch
        {
            EmbyEpisode e => e.ItemId,
            EmbyMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in embyItemId)
        {
            return new PlayoutItemWithPath(
                playoutItem,
                $"http://localhost:{Settings.StreamingPort}/media/emby/{itemId}");
        }

        return new PlayoutItemDoesNotExistOnDisk(path);
    }

    private DeadAirFallbackResult GetDecoDeadAirFallback(Playout playout, DateTimeOffset now)
    {
        DecoEntries decoEntries = decoSelector.GetDecoEntries(playout, now);

        // first, check deco template / active deco
        foreach (Deco templateDeco in decoEntries.TemplateDeco)
        {
            switch (templateDeco.DeadAirFallbackMode)
            {
                case DecoMode.Override:
                    //_logger.LogDebug("Dead air fallback will come from template deco (override)");
                    return new CustomDeadAirFallback(
                        templateDeco.DeadAirFallbackCollectionType,
                        templateDeco.DeadAirFallbackCollectionId,
                        templateDeco.DeadAirFallbackMediaItemId,
                        templateDeco.DeadAirFallbackMultiCollectionId,
                        templateDeco.DeadAirFallbackSmartCollectionId);
                case DecoMode.Disable:
                    //_logger.LogDebug("Dead air fallback is disabled by template deco");
                    return new DisableDeadAirFallback();
                case DecoMode.Inherit:
                    //_logger.LogDebug("Dead air fallback will inherit from playout deco");
                    break;
            }
        }

        // second, check playout deco
        foreach (Deco playoutDeco in decoEntries.PlayoutDeco)
        {
            switch (playoutDeco.DeadAirFallbackMode)
            {
                case DecoMode.Override:
                    //_logger.LogDebug("Dead air fallback will come from playout deco (override)");
                    return new CustomDeadAirFallback(
                        playoutDeco.DeadAirFallbackCollectionType,
                        playoutDeco.DeadAirFallbackCollectionId,
                        playoutDeco.DeadAirFallbackMediaItemId,
                        playoutDeco.DeadAirFallbackMultiCollectionId,
                        playoutDeco.DeadAirFallbackSmartCollectionId);
                case DecoMode.Disable:
                    //_logger.LogDebug("Dead air fallback is disabled by playout deco");
                    return new DisableDeadAirFallback();
                case DecoMode.Inherit:
                    //_logger.LogDebug("Dead air fallback will inherit from channel and/or global setting");
                    break;
            }
        }

        return new InheritDeadAirFallback();
    }

    private abstract record DeadAirFallbackResult;

    private sealed record InheritDeadAirFallback : DeadAirFallbackResult;

    private sealed record DisableDeadAirFallback : DeadAirFallbackResult;

    private sealed record CustomDeadAirFallback(
        CollectionType CollectionType,
        int? CollectionId,
        int? MediaItemId,
        int? MultiCollectionId,
        int? SmartCollectionId) : DeadAirFallbackResult;
}
