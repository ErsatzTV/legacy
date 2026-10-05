using System.Diagnostics;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Humanizer;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Application.MediaSources;

public class ScanLocalLibraryHandler : IRequestHandler<ScanLocalLibrary, Either<BaseError, string>>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IImageFolderScanner _imageFolderScanner;
    private readonly IScannerProxy _scannerProxy;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<ScanLocalLibraryHandler> _logger;
    private readonly IMovieFolderScanner _movieFolderScanner;
    private readonly IMusicVideoFolderScanner _musicVideoFolderScanner;
    private readonly IOtherVideoFolderScanner _otherVideoFolderScanner;
    private readonly IRemoteStreamFolderScanner _remoteStreamFolderScanner;
    private readonly ISongFolderScanner _songFolderScanner;
    private readonly ITelevisionFolderScanner _televisionFolderScanner;

    public ScanLocalLibraryHandler(
        IScannerProxy scannerProxy,
        ILibraryRepository libraryRepository,
        IConfigElementRepository configElementRepository,
        IMovieFolderScanner movieFolderScanner,
        ITelevisionFolderScanner televisionFolderScanner,
        IMusicVideoFolderScanner musicVideoFolderScanner,
        IOtherVideoFolderScanner otherVideoFolderScanner,
        ISongFolderScanner songFolderScanner,
        IImageFolderScanner imageFolderScanner,
        IRemoteStreamFolderScanner remoteStreamFolderScanner,
        ILogger<ScanLocalLibraryHandler> logger)
    {
        _scannerProxy = scannerProxy;
        _libraryRepository = libraryRepository;
        _configElementRepository = configElementRepository;
        _movieFolderScanner = movieFolderScanner;
        _televisionFolderScanner = televisionFolderScanner;
        _musicVideoFolderScanner = musicVideoFolderScanner;
        _otherVideoFolderScanner = otherVideoFolderScanner;
        _songFolderScanner = songFolderScanner;
        _imageFolderScanner = imageFolderScanner;
        _remoteStreamFolderScanner = remoteStreamFolderScanner;
        _logger = logger;
    }

    public Task<Either<BaseError, string>> Handle(ScanLocalLibrary request, CancellationToken cancellationToken) =>
        Validate(request, cancellationToken)
            .MapT(parameters => PerformScan(parameters, cancellationToken).Map(_ => parameters.LocalLibrary.Name))
            .Bind(v => v.ToEitherAsync());

    private async Task<Unit> PerformScan(RequestParameters parameters, CancellationToken cancellationToken)
    {
        (LocalLibrary localLibrary, string ffprobePath, string ffmpegPath, bool forceScan, bool deepScan,
            int libraryRefreshInterval, string baseUrl) = parameters;

        var sw = new Stopwatch();
        sw.Start();

        _scannerProxy.SetBaseUrl(baseUrl);

        var scanned = false;

        for (var i = 0; i < localLibrary.Paths.Count; i++)
        {
            LibraryPath libraryPath = localLibrary.Paths[i];

            decimal progressMin = (decimal)i / localLibrary.Paths.Count;
            decimal progressMax = (decimal)(i + 1) / localLibrary.Paths.Count;

            var lastScan = new DateTimeOffset(libraryPath.LastScan ?? SystemTime.MinValueUtc, TimeSpan.Zero);
            DateTimeOffset nextScan = lastScan + TimeSpan.FromHours(libraryRefreshInterval);
            if (forceScan || libraryRefreshInterval > 0 && nextScan < DateTimeOffset.Now)
            {
                scanned = true;

                Either<BaseError, Unit> result = localLibrary.MediaKind switch
                {
                    LibraryMediaKind.Movies =>
                        await _movieFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.Shows =>
                        await _televisionFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.MusicVideos =>
                        await _musicVideoFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.OtherVideos =>
                        await _otherVideoFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.Songs =>
                        await _songFolderScanner.ScanFolder(
                            libraryPath,
                            ffprobePath,
                            ffmpegPath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.Images =>
                        await _imageFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    LibraryMediaKind.RemoteStreams =>
                        await _remoteStreamFolderScanner.ScanFolder(
                            libraryPath,
                            ffmpegPath,
                            ffprobePath,
                            progressMin,
                            progressMax,
                            deepScan,
                            cancellationToken),
                    _ => Unit.Default
                };

                if (result.IsRight)
                {
                    libraryPath.LastScan = DateTime.UtcNow;
                    await _libraryRepository.UpdateLastScan(libraryPath);
                }
            }

            await _scannerProxy.UpdateProgress(progressMax, cancellationToken);
        }

        sw.Stop();

        if (scanned)
        {
            _logger.LogDebug(
                "Scan of library {Name} completed in {Duration}, deepScan: {DeepScan}",
                localLibrary.Name,
                sw.Elapsed.Humanize(),
                deepScan);
        }
        else
        {
            _logger.LogDebug(
                "Skipping unforced scan of local media library {Name}",
                localLibrary.Name);
        }

        return Unit.Default;
    }

    private async Task<Validation<BaseError, RequestParameters>> Validate(
        ScanLocalLibrary request,
        CancellationToken cancellationToken)
    {
        Validation<BaseError, LocalLibrary> libraryResult = await LocalLibraryMustExist(request);
        Validation<BaseError, string> ffprobePathResult = await ValidateFFprobePath(cancellationToken);
        Validation<BaseError, string> ffmpegPathResult = await ValidateFFmpegPath(cancellationToken);
        Validation<BaseError, int> refreshIntervalResult = await ValidateLibraryRefreshInterval(cancellationToken);

        return (libraryResult, ffprobePathResult, ffmpegPathResult, refreshIntervalResult)
            .Apply((library, ffprobePath, ffmpegPath, libraryRefreshInterval) => new RequestParameters(
                library,
                ffprobePath,
                ffmpegPath,
                request.ForceScan,
                request.DeepScan,
                libraryRefreshInterval,
                request.BaseUrl));
    }

    private Task<Validation<BaseError, LocalLibrary>> LocalLibraryMustExist(ScanLocalLibrary request) =>
        _libraryRepository.GetLibrary(request.LibraryId)
            .Map(maybeLibrary => maybeLibrary.OfType<LocalLibrary>().HeadOrNone())
            .Map(v => v.ToValidation<BaseError>($"Local library {request.LibraryId} does not exist."));

    private Task<Validation<BaseError, string>> ValidateFFprobePath(CancellationToken cancellationToken) =>
        _configElementRepository.GetValue<string>(ConfigElementKey.FFprobePath, cancellationToken)
            .FilterT(File.Exists)
            .Map(ffprobePath =>
                ffprobePath.ToValidation<BaseError>("FFprobe path does not exist on the file system"));

    private Task<Validation<BaseError, string>> ValidateFFmpegPath(CancellationToken cancellationToken) =>
        _configElementRepository.GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken)
            .FilterT(File.Exists)
            .Map(ffmpegPath =>
                ffmpegPath.ToValidation<BaseError>("FFmpeg path does not exist on the file system"));

    private Task<Validation<BaseError, int>> ValidateLibraryRefreshInterval(CancellationToken cancellationToken) =>
        _configElementRepository.GetValue<int>(ConfigElementKey.LibraryRefreshInterval, cancellationToken)
            .FilterT(lri => lri is >= 0 and < 1_000_000)
            .Map(lri => lri.ToValidation<BaseError>("Library refresh interval is invalid"));

    private record RequestParameters(
        LocalLibrary LocalLibrary,
        string FFprobePath,
        string FFmpegPath,
        bool ForceScan,
        bool DeepScan,
        int LibraryRefreshInterval,
        string BaseUrl);
}
