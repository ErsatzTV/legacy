using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Maintenance;

public class DeleteOrphanedSubtitlesHandler(IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<DeleteOrphanedSubtitles, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteOrphanedSubtitles request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            IEnumerable<int> toDelete = await dbContext.Connection.QueryAsync<int>(
                """
                SELECT S.Id FROM Subtitle S
                    WHERE S.ArtistMetadataId IS NULL AND S.EpisodeMetadataId IS NULL
                    AND S.ImageMetadataId IS NULL AND S.MovieMetadataId IS NULL
                    AND S.MusicVideoMetadataId IS NULL AND S.OtherVideoMetadataId IS NULL
                    AND S.RemoteStreamMetadataId IS NULL AND S.SeasonMetadataId IS NULL
                    AND S.ShowMetadataId IS NULL AND S.SongMetadataId IS NULL
                """);

            IEnumerable<int> toDeleteExternal = await dbContext.Connection.QueryAsync<int>(
                """
                SELECT S.Id FROM Subtitle S
                    WHERE S.SubtitleKind = 1 AND (S.Path IS NULL OR S.Path = '')
                """);

            foreach (int id in toDelete.Concat(toDeleteExternal).Distinct())
            {
                await dbContext.Connection.ExecuteAsync("DELETE FROM Subtitle WHERE Id = @Id", new { Id = id });
            }

            return Unit.Default;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}
