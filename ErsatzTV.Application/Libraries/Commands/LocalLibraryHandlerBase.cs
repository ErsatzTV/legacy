using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Core.Libraries.LocalLibraryPaths;

namespace ErsatzTV.Application.Libraries;

public abstract class LocalLibraryHandlerBase
{
    protected static Task<Validation<BaseError, LocalLibrary>> NameMustBeValid(
        ILocalLibraryRequest request,
        LocalLibrary localLibrary) =>
        request.NotEmpty(c => c.Name)
            .Bind(_ => request.NotLongerThan(50)(c => c.Name))
            .Map(_ => localLibrary).AsTask();

    protected static Task<Validation<BaseError, LocalLibrary>> PathsMustBeFullyQualified(LocalLibrary localLibrary) =>
        localLibrary.Paths.Map(lp => lp.Path)
            .Find(path => !IsFullyQualified(path))
            .Match(
                path => Fail<BaseError, LocalLibrary>($"Path [{path}] must be an absolute path"),
                () => Success<BaseError, LocalLibrary>(localLibrary))
            .AsTask();

    protected static async Task<Validation<BaseError, LocalLibrary>> PathsMustBeValid(
        TvContext dbContext,
        LocalLibrary localLibrary,
        int? existingLibraryId = null)
    {
        List<LocalPath> allPaths = await dbContext.LocalLibraries
            .Include(ll => ll.Paths)
            .Filter(ll => existingLibraryId == null || ll.Id != existingLibraryId)
            .ToListAsync()
            .Map(list => list.SelectMany(ll => ll.Paths.Map(lp => new LocalPath(ll.MediaKind, lp.Path))).ToList());

        var localPaths = localLibrary.Paths.Map(lp => new LocalPath(localLibrary.MediaKind, lp.Path)).ToList();

        // List<T>.Find shadows the LanguageExt Find extension
        return Optional(localPaths.Find(folder => allPaths.Any(f => Conflicts(f, folder))))
            .Match(
                folder => Fail<BaseError, LocalLibrary>($"Path [{folder.Path}] must not belong to another library path"),
                () => Success<BaseError, LocalLibrary>(localLibrary));
    }
}
