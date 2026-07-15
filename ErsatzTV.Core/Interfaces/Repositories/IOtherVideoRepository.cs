using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Metadata;

namespace ErsatzTV.Core.Interfaces.Repositories;

public interface IOtherVideoRepository
{
    Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> GetOrAdd(
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> FindOtherVideoPaths(LibraryPath libraryPath);
    Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path);
    Task<bool> AddGenre(OtherVideoMetadata metadata, Genre genre);
    Task<bool> AddTag(OtherVideoMetadata metadata, Tag tag);
    Task<bool> AddStudio(OtherVideoMetadata metadata, Studio studio);
    Task<bool> AddActor(OtherVideoMetadata metadata, Actor actor);
    Task<bool> AddDirector(OtherVideoMetadata metadata, Director director);
    Task<bool> AddWriter(OtherVideoMetadata metadata, Writer writer);
    Task<bool> UpdateTitles(OtherVideoMetadata metadata, string title, string sortTitle);
}
