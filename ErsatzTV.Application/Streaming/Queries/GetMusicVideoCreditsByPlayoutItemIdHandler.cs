using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetMusicVideoCreditsByPlayoutItemIdHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IMusicVideoCreditsGenerator musicVideoCreditsGenerator,
    ILogger<GetMusicVideoCreditsByPlayoutItemIdHandler> logger)
    : IRequestHandler<GetMusicVideoCreditsByPlayoutItemId, Option<string>>
{
    public async Task<Option<string>> Handle(
        GetMusicVideoCreditsByPlayoutItemId request,
        CancellationToken cancellationToken)
    {
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

        var subtitles = new List<Subtitle>();
        foreach (PlayoutItem playoutItem in maybePlayoutItem)
        {
            if (playoutItem.MediaItem is not MusicVideo musicVideo)
            {
                break;
            }

            switch (playoutItem.Playout.Channel.MusicVideoCreditsMode)
            {
                case ChannelMusicVideoCreditsMode.GenerateSubtitles:
                    string templateName = playoutItem.Playout.Channel.MusicVideoCreditsTemplate;
                    if (!string.IsNullOrWhiteSpace(templateName))
                    {
                        var fileWithExtension = $"{templateName}.sbntxt";
                        subtitles.AddRange(
                            await musicVideoCreditsGenerator.GenerateCreditsSubtitleFromTemplate(
                                musicVideo,
                                playoutItem.Playout.Channel.FFmpegProfile,
                                request.SeekToMs.Map(TimeSpan.FromMilliseconds),
                                Path.Combine(FileSystemLayout.MusicVideoCreditsTemplatesFolder, fileWithExtension)));
                    }
                    else
                    {
                        logger.LogWarning(
                            "Music video credits template {Template} does not exist; falling back to built-in template",
                            templateName);

                        subtitles.AddRange(
                            await musicVideoCreditsGenerator.GenerateCreditsSubtitle(
                                musicVideo,
                                playoutItem.Playout.Channel.FFmpegProfile));
                    }

                    break;
                case ChannelMusicVideoCreditsMode.None:
                default:
                    break;
            }
        }

        return subtitles.HeadOrNone().Map(s => s.Path);
    }
}
