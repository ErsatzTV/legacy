using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class MovieRepository : IMovieRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<MovieRepository> _logger;

    public MovieRepository(IDbContextFactory<TvContext> dbContextFactory, ILogger<MovieRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<bool> AllMoviesExist(List<int> movieIds)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM Movie WHERE Id in @MovieIds",
                new { MovieIds = movieIds })
            .Map(c => c == movieIds.Count);
    }

    public async Task<Option<Movie>> GetMovie(int movieId)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Movies
            .Include(m => m.MovieMetadata)
            .ThenInclude(m => m.Artwork)
            .Include(m => m.MovieMetadata)
            .ThenInclude(m => m.Genres)
            .Include(m => m.MovieMetadata)
            .ThenInclude(m => m.Tags)
            .Include(m => m.MovieMetadata)
            .ThenInclude(m => m.Studios)
            .Include(m => m.MovieMetadata)
            .ThenInclude(m => m.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Directors)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Writers)
            .Include(m => m.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(m => m.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .OrderBy(m => m.Id)
            .SingleOrDefaultAsync(m => m.Id == movieId)
            .Map(Optional);
    }

    public async Task<Either<BaseError, MediaItemScanResult<Movie>>> GetOrAdd(
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<Movie> maybeExisting = await dbContext.Movies
            .Filter(m => !(m is PlexMovie) && !(m is JellyfinMovie) && !(m is EmbyMovie))
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Artwork)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Genres)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Tags)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Studios)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Guids)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Actors)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Directors)
            .Include(i => i.MovieMetadata)
            .ThenInclude(mm => mm.Writers)
            .Include(i => i.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(i => i.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .OrderBy(i => i.MediaVersions.First().MediaFiles.First().Path)
            .SingleOrDefaultAsync(i => i.MediaVersions.First().MediaFiles.First().Path == path, cancellationToken);

        return await maybeExisting.Match(
            mediaItem =>
                Right<BaseError, MediaItemScanResult<Movie>>(
                    new MediaItemScanResult<Movie>(mediaItem) { IsAdded = false }).AsTask(),
            async () => await AddMovie(dbContext, libraryPath.Id, libraryFolder.Id, path, cancellationToken));
    }

    public async Task<IEnumerable<string>> FindMoviePaths(LibraryPath libraryPath)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<string>(
            @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN Movie M on MV.MovieId = M.Id
                INNER JOIN MediaItem MI on M.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId",
            new { LibraryPathId = libraryPath.Id });
    }

    public async Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        List<int> ids = await dbContext.Connection.QueryAsync<int>(
                @"SELECT M.Id
                FROM Movie M
                INNER JOIN MediaItem MI on M.Id = MI.Id
                INNER JOIN MediaVersion MV on M.Id = MV.MovieId
                INNER JOIN MediaFile MF on MV.Id = MF.MediaVersionId
                WHERE MI.LibraryPathId = @LibraryPathId AND MF.Path = @Path",
                new { LibraryPathId = libraryPath.Id, Path = path })
            .Map(result => result.ToList());

        foreach (int movieId in ids)
        {
            Movie movie = await dbContext.Movies.FindAsync(movieId);
            if (movie != null)
            {
                dbContext.Movies.Remove(movie);
            }
        }

        bool changed = await dbContext.SaveChangesAsync() > 0;
        return changed ? ids : new List<int>();
    }

    public async Task<bool> AddGenre(MovieMetadata metadata, Genre genre)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Genre (Name, MovieMetadataId) VALUES (@Name, @MetadataId)",
            new { genre.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddTag(MovieMetadata metadata, Tag tag)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Tag (Name, MovieMetadataId, ExternalCollectionId) VALUES (@Name, @MetadataId, @ExternalCollectionId)",
            new { tag.Name, MetadataId = metadata.Id, tag.ExternalCollectionId }).Map(result => result > 0);
    }

    public async Task<bool> AddStudio(MovieMetadata metadata, Studio studio)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Studio (Name, MovieMetadataId) VALUES (@Name, @MetadataId)",
            new { studio.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddActor(MovieMetadata metadata, Actor actor)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        int? artworkId = null;

        if (actor.Artwork != null)
        {
            artworkId = await dbContext.Connection.QuerySingleAsync<int>(
                $"""
                 INSERT INTO Artwork (ArtworkKind, DateAdded, DateUpdated, Path)
                     VALUES (@ArtworkKind, @DateAdded, @DateUpdated, @Path);
                 SELECT {TvContext.LastInsertedRowId}
                 """,
                new
                {
                    ArtworkKind = (int)actor.Artwork.ArtworkKind,
                    actor.Artwork.DateAdded,
                    actor.Artwork.DateUpdated,
                    actor.Artwork.Path
                });
        }

        return await dbContext.Connection.ExecuteAsync(
                "INSERT INTO Actor (Name, Role, `Order`, MovieMetadataId, ArtworkId) VALUES (@Name, @Role, @Order, @MetadataId, @ArtworkId)",
                new { actor.Name, actor.Role, actor.Order, MetadataId = metadata.Id, ArtworkId = artworkId })
            .Map(result => result > 0);
    }

    public async Task<bool> UpdateTitles(MovieMetadata movieMetadata, string title, string sortTitle)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            @"UPDATE MovieMetadata SET Title = @Title, SortTitle = @SortTitle WHERE Id = @Id",
            new { Title = title, SortTitle = sortTitle, movieMetadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddDirector(MovieMetadata metadata, Director director)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Director (Name, MovieMetadataId) VALUES (@Name, @MetadataId)",
            new { director.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddWriter(MovieMetadata metadata, Writer writer)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Writer (Name, MovieMetadataId) VALUES (@Name, @MetadataId)",
            new { writer.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    private async Task<Either<BaseError, MediaItemScanResult<Movie>>> AddMovie(
        TvContext dbContext,
        int libraryPathId,
        int libraryFolderId,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await MediaItemRepository.MediaFileAlreadyExists(
                    path,
                    libraryPathId,
                    dbContext,
                    _logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            var movie = new Movie
            {
                LibraryPathId = libraryPathId,
                MediaVersions =
                [
                    new MediaVersion
                    {
                        MediaFiles =
                        [
                            new MediaFile
                            {
                                Path = path, PathHash = PathUtils.GetPathHash(path), LibraryFolderId = libraryFolderId
                            }
                        ],
                        Streams = []
                    }
                ],
                TraktListItems = []
            };
            await dbContext.Movies.AddAsync(movie, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(movie).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(movie.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<Movie>(movie) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}
