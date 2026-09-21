using System.Threading.Channels;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Application.Libraries.Mapper;

namespace ErsatzTV.Application.Libraries;

public class CreateLocalLibraryHandler : LocalLibraryHandlerBase,
    IRequestHandler<CreateLocalLibrary, Either<BaseError, LocalLibraryViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IEntityLocker _entityLocker;
    private readonly ChannelWriter<IScannerBackgroundServiceRequest> _scannerWorkerChannel;

    public CreateLocalLibraryHandler(
        ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel,
        IEntityLocker entityLocker,
        IDbContextFactory<TvContext> dbContextFactory)
    {
        _scannerWorkerChannel = scannerWorkerChannel;
        _entityLocker = entityLocker;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Either<BaseError, LocalLibraryViewModel>> Handle(
        CreateLocalLibrary request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, LocalLibrary> validation = await Validate(dbContext, request);
        return await validation.Apply(localLibrary => PersistLocalLibrary(dbContext, localLibrary));
    }

    private async Task<LocalLibraryViewModel> PersistLocalLibrary(
        TvContext dbContext,
        LocalLibrary localLibrary)
    {
        await dbContext.LocalLibraries.AddAsync(localLibrary);
        await dbContext.SaveChangesAsync();

        if (_entityLocker.LockLibrary(localLibrary.Id))
        {
            await _scannerWorkerChannel.WriteAsync(new ForceScanLocalLibrary(localLibrary.Id));
        }

        return ProjectToViewModel(localLibrary);
    }

    private static Task<Validation<BaseError, LocalLibrary>> Validate(
        TvContext dbContext,
        CreateLocalLibrary request) =>
        MediaSourceMustExist(dbContext, request)
            .BindT(localLibrary => NameMustBeValid(request, localLibrary))
            .BindT(PathsMustBeFullyQualified)
            .BindT(localLibrary => PathsMustBeValid(dbContext, localLibrary));

    private static Task<Validation<BaseError, LocalLibrary>> MediaSourceMustExist(
        TvContext dbContext,
        CreateLocalLibrary request) =>
        dbContext.LocalMediaSources
            .OrderBy(lms => lms.Id)
            .FirstOrDefaultAsync()
            .Map(Optional)
            .MapT(lms => new LocalLibrary
            {
                Name = request.Name,
                Paths = request.Paths.Map(p => new LibraryPath { Path = p }).ToList(),
                MediaKind = request.MediaKind,
                MediaSourceId = lms.Id
            })
            .Map(o => o.ToValidation<BaseError>("LocalMediaSource does not exist."));
}
