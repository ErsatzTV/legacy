using System.Threading.Channels;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Playouts;

// next reads serialized playout files, so edits to anything they embed must re-sync channels.
// keep playout lookups in step with IncludeForNextPlayout. resolve deletes before removing rows;
// cascades remove the references these lookups follow.
public static class NextPlayoutDependents
{
    public static async Task<List<string>> ForWatermark(
        TvContext dbContext,
        int watermarkId,
        CancellationToken cancellationToken)
    {
        Option<int> maybeGlobalWatermarkId = await dbContext.ConfigElements
            .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken);
        if (maybeGlobalWatermarkId.Exists(id => id == watermarkId))
        {
            return await All(dbContext, cancellationToken);
        }

        List<int> channelIds = await dbContext.Channels
            .Where(c => c.WatermarkId == watermarkId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        List<int> itemChannelIds = await dbContext.Set<PlayoutItemWatermark>()
            .Where(piw => piw.WatermarkId == watermarkId)
            .Select(piw => piw.PlayoutItem.Playout.ChannelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<int> decoIds = await dbContext.Decos
            .Where(d => d.DecoWatermarks.Any(dw => dw.WatermarkId == watermarkId))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        List<int> decoChannelIds = await PlayoutChannelIdsForDecos(dbContext, decoIds, cancellationToken);

        return await WithMirrors(dbContext, [..channelIds, ..itemChannelIds, ..decoChannelIds], cancellationToken);
    }

    public static async Task<List<string>> ForDeco(
        TvContext dbContext,
        int decoId,
        CancellationToken cancellationToken)
    {
        List<int> channelIds = await PlayoutChannelIdsForDecos(dbContext, [decoId], cancellationToken);
        return await WithMirrors(dbContext, channelIds, cancellationToken);
    }

    public static async Task<List<string>> ForDecoTemplate(
        TvContext dbContext,
        int decoTemplateId,
        CancellationToken cancellationToken)
    {
        List<int> channelIds = await dbContext.Playouts
            .Where(p => p.Templates.Any(t => t.DecoTemplateId == decoTemplateId))
            .Select(p => p.ChannelId)
            .ToListAsync(cancellationToken);

        return await WithMirrors(dbContext, channelIds, cancellationToken);
    }

    public static async Task<List<string>> ForPlayout(
        TvContext dbContext,
        int playoutId,
        CancellationToken cancellationToken)
    {
        List<int> channelIds = await dbContext.Playouts
            .Where(p => p.Id == playoutId)
            .Select(p => p.ChannelId)
            .ToListAsync(cancellationToken);

        return await WithMirrors(dbContext, channelIds, cancellationToken);
    }

    public static async Task<List<string>> ForMediaSource(
        TvContext dbContext,
        int mediaSourceId,
        CancellationToken cancellationToken)
    {
        List<int> channelIds = await dbContext.PlayoutItems
            .Where(pi => pi.MediaItem.LibraryPath.Library.MediaSourceId == mediaSourceId)
            .Select(pi => pi.Playout.ChannelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return await WithMirrors(dbContext, channelIds, cancellationToken);
    }

    public static Task<List<string>> ForFFmpegProfile(
        TvContext dbContext,
        int ffmpegProfileId,
        CancellationToken cancellationToken) =>
        dbContext.Channels
            .Where(c => c.FFmpegProfileId == ffmpegProfileId)
            .Select(c => c.Number)
            .ToListAsync(cancellationToken);

    public static Task<List<string>> All(TvContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Channels
            .Select(c => c.Number)
            .ToListAsync(cancellationToken);

    public static async Task QueueSyncNextPlayouts(
        this ChannelWriter<IBackgroundServiceRequest> workerChannel,
        IEnumerable<string> channelNumbers,
        CancellationToken cancellationToken)
    {
        foreach (string channelNumber in channelNumbers.Distinct())
        {
            await workerChannel.WriteAsync(new SyncNextPlayout(channelNumber), cancellationToken);
        }
    }

    private static Task<List<int>> PlayoutChannelIdsForDecos(
        TvContext dbContext,
        List<int> decoIds,
        CancellationToken cancellationToken) =>
        dbContext.Playouts
            .Where(p => (p.DecoId != null && decoIds.Contains(p.DecoId.Value)) ||
                        p.Templates.Any(t => t.DecoTemplate.Items.Any(i => decoIds.Contains(i.DecoId))))
            .Select(p => p.ChannelId)
            .ToListAsync(cancellationToken);

    // mirrors serialize their source channel's items
    private static async Task<List<string>> WithMirrors(
        TvContext dbContext,
        List<int> channelIds,
        CancellationToken cancellationToken)
    {
        if (channelIds.Count == 0)
        {
            return [];
        }

        var distinctIds = channelIds.Distinct().ToList();

        return await dbContext.Channels
            .Where(c => distinctIds.Contains(c.Id) ||
                        (c.MirrorSourceChannelId != null && distinctIds.Contains(c.MirrorSourceChannelId.Value)))
            .Select(c => c.Number)
            .ToListAsync(cancellationToken);
    }
}
