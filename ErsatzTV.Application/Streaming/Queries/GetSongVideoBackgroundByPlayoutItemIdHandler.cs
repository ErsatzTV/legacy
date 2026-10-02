using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetSongVideoBackgroundByPlayoutItemIdHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ISongVideoGenerator songVideoGenerator,
    ITroubleshootingPlayoutItemStore troubleshootingPlayoutItemStore,
    ILogger<GetSongVideoBackgroundByPlayoutItemIdHandler> logger)
    : IRequestHandler<GetSongVideoBackgroundByPlayoutItemId, Option<string>>
{
    public async Task<Option<string>> Handle(
        GetSongVideoBackgroundByPlayoutItemId request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            Validation<BaseError, Tuple<string, string>> validationResult = (
                    await FFmpegPathMustExist(dbContext, cancellationToken),
                    await FFprobePathMustExist(dbContext, cancellationToken))
                .Apply((ffmpeg, ffprobe) => Tuple(ffmpeg, ffprobe));

            foreach ((string ffmpeg, string ffprobe) in validationResult.SuccessToSeq())
            {
                Song song = null;
                Channel channel = null;
                int? seed = null;

                if (request.PlayoutItemId == TroubleshootingPlayoutItem.PlayoutItemId)
                {
                    foreach (TroubleshootingPlayoutItem current in troubleshootingPlayoutItemStore.Current())
                    {
                        song = current.PlayoutItem.MediaItem as Song;
                        channel = current.Channel;
                    }
                }
                else
                {
                    PlayoutItem playoutItem = await dbContext.PlayoutItems
                        .AsNoTracking()
                        .Include(pi => pi.MediaItem)
                        .ThenInclude(mi => (mi as Song).SongMetadata)
                        .ThenInclude(sm => sm.Artwork)
                        .SingleOrDefaultAsync(pi => pi.Id == request.PlayoutItemId, cancellationToken);

                    // not the playout's channel: mirror channels play this item with their own profile
                    channel = await dbContext.Channels
                        .AsNoTracking()
                        .Include(c => c.FFmpegProfile)
                        .ThenInclude(ff => ff.Resolution)
                        .SingleOrDefaultAsync(c => c.Id == request.ChannelId, cancellationToken);

                    song = playoutItem?.MediaItem as Song;
                    seed = request.PlayoutItemId;
                }

                if (song is null || channel is null)
                {
                    return None;
                }

                Tuple<string, MediaVersion> result = await songVideoGenerator.GenerateSongVideo(
                    song,
                    channel,
                    ffmpeg,
                    ffprobe,
                    seed,
                    cancellationToken);

                return result.Item1;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get song video background");
        }

        return None;
    }

    private static Task<Validation<BaseError, string>> FFmpegPathMustExist(
        TvContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken)
            .FilterT(File.Exists)
            .Map(maybePath => maybePath.ToValidation<BaseError>("FFmpeg path does not exist on filesystem"));

    private static Task<Validation<BaseError, string>> FFprobePathMustExist(
        TvContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFprobePath, cancellationToken)
            .FilterT(File.Exists)
            .Map(maybePath => maybePath.ToValidation<BaseError>("FFprobe path does not exist on filesystem"));
}
