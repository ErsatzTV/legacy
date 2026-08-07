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

            // only local sidecars need a path; media server sidecars are fetched by stream index
            IEnumerable<int> toDeleteExternal = await dbContext.Connection.QueryAsync<int>(
                """
                SELECT S.Id FROM Subtitle S
                    WHERE S.SubtitleKind = 1 AND (S.Path IS NULL OR S.Path = '')
                    AND S.Id IN (
                        SELECT S2.Id FROM Subtitle S2
                            INNER JOIN MovieMetadata MM ON MM.Id = S2.MovieMetadataId
                            INNER JOIN MediaItem MI ON MI.Id = MM.MovieId
                            INNER JOIN LibraryPath LP ON LP.Id = MI.LibraryPathId
                            INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                        UNION
                        SELECT S2.Id FROM Subtitle S2
                            INNER JOIN EpisodeMetadata EM ON EM.Id = S2.EpisodeMetadataId
                            INNER JOIN MediaItem MI ON MI.Id = EM.EpisodeId
                            INNER JOIN LibraryPath LP ON LP.Id = MI.LibraryPathId
                            INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                        UNION
                        SELECT S2.Id FROM Subtitle S2
                            INNER JOIN MusicVideoMetadata MVM ON MVM.Id = S2.MusicVideoMetadataId
                            INNER JOIN MediaItem MI ON MI.Id = MVM.MusicVideoId
                            INNER JOIN LibraryPath LP ON LP.Id = MI.LibraryPathId
                            INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                        UNION
                        SELECT S2.Id FROM Subtitle S2
                            INNER JOIN OtherVideoMetadata OVM ON OVM.Id = S2.OtherVideoMetadataId
                            INNER JOIN MediaItem MI ON MI.Id = OVM.OtherVideoId
                            INNER JOIN LibraryPath LP ON LP.Id = MI.LibraryPathId
                            INNER JOIN LocalLibrary LL ON LL.Id = LP.LibraryId
                    )
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
