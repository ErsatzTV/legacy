using System.Globalization;
using ErsatzTV.Application.Libraries;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Runtime;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.MediaSources;

public class CallLocalLibraryScannerHandler : CallLibraryScannerHandler<IScanLocalLibrary>,
    IRequestHandler<ForceScanLocalLibrary, Either<BaseError, string>>,
    IRequestHandler<ScanLocalLibraryIfNeeded, Either<BaseError, string>>
{
    private readonly IScannerProxyService _scannerProxyService;

    public CallLocalLibraryScannerHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IConfigElementRepository configElementRepository,
        IScannerProxyService scannerProxyService,
        IRuntimeInfo runtimeInfo,
        ILogger<CallLocalLibraryScannerHandler> logger)
        : base(dbContextFactory, configElementRepository, runtimeInfo, logger)
    {
        _scannerProxyService = scannerProxyService;
    }

    Task<Either<BaseError, string>> IRequestHandler<ForceScanLocalLibrary, Either<BaseError, string>>.Handle(
        ForceScanLocalLibrary request,
        CancellationToken cancellationToken) => Handle(request, cancellationToken);

    Task<Either<BaseError, string>> IRequestHandler<ScanLocalLibraryIfNeeded, Either<BaseError, string>>.Handle(
        ScanLocalLibraryIfNeeded request,
        CancellationToken cancellationToken) => Handle(request, cancellationToken);

    private async Task<Either<BaseError, string>> Handle(IScanLocalLibrary request, CancellationToken cancellationToken)
    {
        Validation<BaseError, ScanParameters> validation = await Validate(request, cancellationToken);
        return await validation.Match(
            parameters => PerformScan(parameters, request, cancellationToken),
            error =>
            {
                foreach (ScanIsNotRequired scanIsNotRequired in error.OfType<ScanIsNotRequired>())
                {
                    return Task.FromResult<Either<BaseError, string>>(scanIsNotRequired);
                }

                return Task.FromResult<Either<BaseError, string>>(error.Join());
            });
    }

    private async Task<Either<BaseError, string>> PerformScan(
        ScanParameters parameters,
        IScanLocalLibrary request,
        CancellationToken cancellationToken)
    {
        Option<Guid> maybeScanId = _scannerProxyService.StartScan(request.LibraryId);
        foreach (var scanId in maybeScanId)
        {
            try
            {
                var arguments = new List<string>
                {
                    "scan-local",
                    request.LibraryId.ToString(CultureInfo.InvariantCulture),
                    GetBaseUrl(scanId)
                };

                if (request.ForceScan)
                {
                    arguments.Add("--force");
                }

                if (request.DeepScan)
                {
                    arguments.Add("--deep");
                }

                return await base.PerformScan(parameters, arguments, cancellationToken);
            }
            finally
            {
                _scannerProxyService.EndScan(scanId);
            }
        }

        return BaseError.New($"Library {request.LibraryId} is already scanning");
    }

    protected override async Task<Tuple<string, DateTimeOffset>> GetLastScan(
        TvContext dbContext,
        IScanLocalLibrary request,
        CancellationToken cancellationToken)
    {
        List<LibraryPath> libraryPaths = await dbContext.LibraryPaths
            .Filter(lp => lp.LibraryId == request.LibraryId)
            .ToListAsync(cancellationToken);

        DateTime minDateTime = libraryPaths.Count != 0
            ? libraryPaths.Min(lp => lp.LastScan ?? SystemTime.MinValueUtc)
            : SystemTime.MaxValueUtc;

        string libraryName = await dbContext.Libraries
            .SelectOneAsync(l => l.Id, l => l.Id == request.LibraryId, cancellationToken)
            .Match(l => l.Name, () => string.Empty);

        return new Tuple<string, DateTimeOffset>(libraryName, new DateTimeOffset(minDateTime, TimeSpan.Zero));
    }

    protected override bool ScanIsRequired(
        DateTimeOffset lastScan,
        int libraryRefreshInterval,
        IScanLocalLibrary request)
    {
        if (lastScan == SystemTime.MaxValueUtc)
        {
            return false;
        }

        DateTimeOffset nextScan = lastScan + TimeSpan.FromHours(libraryRefreshInterval);
        return request.ForceScan || libraryRefreshInterval > 0 && nextScan < DateTimeOffset.Now;
    }
}
