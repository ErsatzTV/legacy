using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class RemoteStreamRepository(
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<RemoteStreamRepository> logger)
    : IRemoteStreamRepository
{
    public async Task<Either<BaseError, MediaItemScanResult<RemoteStream>>> GetOrAdd(
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<RemoteStream> maybeExisting = await dbContext.RemoteStreams
            .AsNoTracking()
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Genres)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Tags)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Studios)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Guids)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Actors)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(i => i.RemoteStreamMetadata)
            .ThenInclude(rsm => rsm.Artwork)
            .Include(rs => rs.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(rs => rs.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(rs => rs.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(rs => rs.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .OrderBy(i => i.MediaVersions.First().MediaFiles.First().Path)
            .SingleOrDefaultAsync(i => i.MediaVersions.First().MediaFiles.First().Path == path, cancellationToken);

        return await maybeExisting.Match(
            mediaItem =>
                Right<BaseError, MediaItemScanResult<RemoteStream>>(
                    new MediaItemScanResult<RemoteStream>(mediaItem) { IsAdded = false }).AsTask(),
            async () => await AddRemoteStream(dbContext, libraryPath.Id, libraryFolder.Id, path, cancellationToken));
    }

    public async Task<IEnumerable<string>> FindRemoteStreamPaths(
        LibraryPath libraryPath,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<string>(
            new CommandDefinition(
                @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN RemoteStream O on MV.RemoteStreamId = O.Id
                INNER JOIN MediaItem MI on O.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId",
                parameters: new { LibraryPathId = libraryPath.Id },
                cancellationToken: cancellationToken));
    }

    public async Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
            new CommandDefinition(
                @"SELECT O.Id
                    FROM RemoteStream O
                    INNER JOIN MediaItem MI on O.Id = MI.Id
                    INNER JOIN MediaVersion MV on O.Id = MV.RemoteStreamId
                    INNER JOIN MediaFile MF on MV.Id = MF.MediaVersionId
                    WHERE MI.LibraryPathId = @LibraryPathId AND MF.Path = @Path",
                parameters: new { LibraryPathId = libraryPath.Id, Path = path },
                cancellationToken: cancellationToken)).Map(result => result.ToList());

        foreach (int remoteStreamId in ids)
        {
            Option<RemoteStream> maybeRemoteStream = await dbContext.RemoteStreams
                .SelectOneAsync(rs => rs.Id, rs => rs.Id == remoteStreamId, cancellationToken);
            foreach (var remoteStream in maybeRemoteStream)
            {
                dbContext.RemoteStreams.Remove(remoteStream);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ids;
    }

    public async Task<bool> AddTag(RemoteStreamMetadata metadata, Tag tag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO Tag (Name, RemoteStreamMetadataId, ExternalCollectionId, ExternalTypeId) VALUES (@Name, @MetadataId, @ExternalCollectionId, @ExternalTypeId)",
                parameters: new { tag.Name, MetadataId = metadata.Id, tag.ExternalCollectionId, tag.ExternalTypeId },
                cancellationToken: cancellationToken)).Map(result => result > 0);
    }

    public async Task UpdateDefinition(RemoteStream remoteStream, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.RemoteStreams
            .Where(rs => rs.Id == remoteStream.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(rs => rs.Url, remoteStream.Url)
                    .SetProperty(rs => rs.Script, remoteStream.Script)
                    .SetProperty(rs => rs.Duration, remoteStream.Duration)
                    .SetProperty(rs => rs.FallbackQuery, remoteStream.FallbackQuery)
                    .SetProperty(rs => rs.IsLive, remoteStream.IsLive),
                cancellationToken);
    }

    private async Task<Either<BaseError, MediaItemScanResult<RemoteStream>>> AddRemoteStream(
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
                    logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            var remoteStream = new RemoteStream
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
                TraktListItems = new List<TraktListItem>
                {
                    Capacity = 0
                }
            };

            await dbContext.RemoteStreams.AddAsync(remoteStream, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(remoteStream).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(remoteStream.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<RemoteStream>(remoteStream) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}
