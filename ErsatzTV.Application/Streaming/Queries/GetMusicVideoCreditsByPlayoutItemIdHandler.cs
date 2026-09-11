using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetMusicVideoCreditsByPlayoutItemIdHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IMusicVideoCreditsGenerator musicVideoCreditsGenerator,
    ITroubleshootingPlayoutItemStore troubleshootingPlayoutItemStore,
    IFileSystem fileSystem,
    ILogger<GetMusicVideoCreditsByPlayoutItemIdHandler> logger)
    : IRequestHandler<GetMusicVideoCreditsByPlayoutItemId, Option<string>>
{
    public async Task<Option<string>> Handle(
        GetMusicVideoCreditsByPlayoutItemId request,
        CancellationToken cancellationToken)
    {
        if (request.PlayoutItemId == MusicVideoCreditsSubtitle.TroubleshootingPlayoutItemId)
        {
            foreach (TroubleshootingPlayoutItem item in troubleshootingPlayoutItemStore.Current())
            {
                if (item.PlayoutItem.MediaItem is not MusicVideo musicVideo)
                {
                    return None;
                }

                Option<string> maybePath = await Generate(musicVideo, item.Channel, request.SeekToMs);
                foreach (string path in maybePath)
                {
                    fileSystem.File.Copy(
                        path,
                        Path.Combine(FileSystemLayout.TranscodeTroubleshootingFolder, "music-video-credits.ass"),
                        overwrite: true);
                }

                return maybePath;
            }

            return None;
        }

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<PlayoutItem> maybePlayoutItem = await dbContext.PlayoutItems
            .AsNoTracking()
            .Include(pi => pi.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artists)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).Artist)
            .ThenInclude(mv => mv.ArtistMetadata)
            .Include(pi => pi.Playout)
            .ThenInclude(p => p.Channel)
            .ThenInclude(c => c.FFmpegProfile)
            .ThenInclude(ff => ff.Resolution)
            .SingleOrDefaultAsync(pi => pi.Id == request.PlayoutItemId, cancellationToken)
            .Map(Optional);

        foreach (PlayoutItem playoutItem in maybePlayoutItem)
        {
            if (playoutItem.MediaItem is MusicVideo musicVideo)
            {
                return await Generate(musicVideo, playoutItem.Playout.Channel, request.SeekToMs);
            }
        }

        return None;
    }

    private async Task<Option<string>> Generate(MusicVideo musicVideo, Channel channel, Option<long> seekToMs)
    {
        if (channel.MusicVideoCreditsMode is not ChannelMusicVideoCreditsMode.GenerateSubtitles)
        {
            return None;
        }

        Option<Subtitle> maybeSubtitle;

        string templateName = channel.MusicVideoCreditsTemplate;
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            var fileWithExtension = $"{templateName}.sbntxt";
            maybeSubtitle = await musicVideoCreditsGenerator.GenerateCreditsSubtitleFromTemplate(
                musicVideo,
                channel.FFmpegProfile,
                seekToMs.Map(TimeSpan.FromMilliseconds),
                Path.Combine(FileSystemLayout.MusicVideoCreditsTemplatesFolder, fileWithExtension));
        }
        else
        {
            logger.LogWarning(
                "Music video credits template {Template} does not exist; falling back to built-in template",
                templateName);

            maybeSubtitle = await musicVideoCreditsGenerator.GenerateCreditsSubtitle(
                musicVideo,
                channel.FFmpegProfile);
        }

        return maybeSubtitle.Map(s => s.Path);
    }
}
