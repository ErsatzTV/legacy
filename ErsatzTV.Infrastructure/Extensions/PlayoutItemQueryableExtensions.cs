using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Extensions;

public static class PlayoutItemQueryableExtensions
{
    public static Task<Option<PlayoutItem>> ForChannelAndTime(
        this IQueryable<PlayoutItem> dbSet,
        int channelId,
        DateTimeOffset time) =>
        dbSet.Filter(pi => pi.Playout.ChannelId == channelId)
            .Filter(pi => pi.Start <= time.UtcDateTime && pi.Finish > time.UtcDateTime)
            .OrderBy(pi => pi.Start)
            .FirstOrDefaultAsync()
            .Map(Optional);

    /// <summary>
    /// Everything PlayoutItemConverter.ToNext and the graphics selectors need: playout/template
    /// decos with watermarks and graphics elements, item watermarks and graphics elements, and
    /// media versions/streams/metadata for each playable media item type.
    /// </summary>
    public static IQueryable<PlayoutItem> IncludeForNextPlayout(this IQueryable<PlayoutItem> dbSet) =>
        dbSet
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)
            .Include(i => i.Watermarks)
            .Include(i => i.PlayoutItemGraphicsElements)
            .ThenInclude(pige => pige.GraphicsElement)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)
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
            .ThenInclude(em => em.Subtitles);
}
