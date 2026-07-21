using System.Runtime.InteropServices;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using ErsatzTV.Scanner.Core.Metadata;
using ErsatzTV.Scanner.Tests.Core.Fakes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Serilog;
using Shouldly;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace ErsatzTV.Scanner.Tests.Core.Metadata;

[TestFixture]
public class MovieFolderScannerTests
{
    private static readonly string FakeRoot = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @"C:\Movies"
        : "/movies";

    private static readonly string FFmpegPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @"C:\bin\ffmpeg.exe"
        : "/bin/ffmpeg";

    private static readonly string FFprobePath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @"C:\bin\ffprobe.exe"
        : "/bin/ffprobe";

    private static readonly ILogger<MovieFolderScanner> Logger;

    static MovieFolderScannerTests()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        ILoggerFactory loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);

        Logger = loggerFactory.CreateLogger<MovieFolderScanner>();
    }

    [TestFixture]
    public class ScanFolder
    {
        [SetUp]
        public void SetUp()
        {
            _movieRepository = Substitute.For<IMovieRepository>();
            _movieRepository.GetOrAdd(Arg.Any<LibraryPath>(), Arg.Any<LibraryFolder>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(args =>
                    Right<BaseError, MediaItemScanResult<Movie>>(new FakeMovieWithPath(args.Arg<string>() ?? string.Empty))
                        .AsTask());
            _movieRepository.FindMoviePaths(Arg.Any<LibraryPath>())
                .Returns(new List<string>().AsEnumerable().AsTask());

            _mediaItemRepository = Substitute.For<IMediaItemRepository>();
            _mediaItemRepository.FlagFileNotFound(Arg.Any<LibraryPath>(), Arg.Any<string>())
                .Returns(new List<int>().AsTask());

            _localStatisticsProvider = Substitute.For<ILocalStatisticsProvider>();
            _localMetadataProvider = Substitute.For<ILocalMetadataProvider>();

            _localStatisticsProvider.RefreshStatistics(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MediaItem>())
                .Returns(Right<BaseError, bool>(true).AsTask());

            // fallback metadata adds metadata to a movie, so we need to replicate that here
            _localMetadataProvider.RefreshFallbackMetadata(Arg.Any<Movie>())
                .Returns(arg =>
                {
                    (arg.Arg<MediaItem>() as Movie ?? new Movie()).MovieMetadata = [new MovieMetadata { Artwork = [] }];
                    return Task.FromResult(true);
                });

            _imageCache = Substitute.For<IImageCache>();

            _libraryRepository = Substitute.For<ILibraryRepository>();
            _libraryRepository.GetOrAddFolder(Arg.Any<LibraryPath>(), Arg.Any<Option<int>>(), Arg.Any<string>())
                .Returns(new LibraryFolder());

            _scannerProxy = Substitute.For<IScannerProxy>();
            _scannerProxy.UpdateProgress(Arg.Any<decimal>(), Arg.Any<CancellationToken>()).Returns(true);
            _scannerProxy.ReindexMediaItems(Arg.Any<int[]>(), Arg.Any<CancellationToken>()).Returns(true);
            _scannerProxy.RemoveMediaItems(Arg.Any<int[]>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        private IMovieRepository _movieRepository;
        private IMediaItemRepository _mediaItemRepository;
        private ILocalStatisticsProvider _localStatisticsProvider;
        private ILocalMetadataProvider _localMetadataProvider;
        private IImageCache _imageCache;
        private ILibraryRepository _libraryRepository;
        private IScannerProxy _scannerProxy;

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_MixedCase(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            char[] mixedCaseExtension = videoExtension.ToLowerInvariant().ToArray();
            mixedCaseExtension[2] = char.ToUpper(mixedCaseExtension[2]);
            videoExtension = new string(mixedCaseExtension);

            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task NewMovie_Statistics_And_SidecarMetadata_MovieNameNfo(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string metadataPath = Path.ChangeExtension(moviePath, "nfo");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(metadataPath)
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshSidecarMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath),
                metadataPath);
        }

        [Test]
        public async Task NewMovie_Statistics_And_SidecarMetadata_MovieNfo(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string metadataPath = Path.Combine(Path.GetDirectoryName(moviePath) ?? string.Empty, "movie.nfo");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(metadataPath)
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshSidecarMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath),
                metadataPath);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_Poster(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string posterPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"poster.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(posterPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(posterPath, ArtworkKind.Poster);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_FolderPoster(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string posterPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"folder.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(posterPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(posterPath, ArtworkKind.Poster);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_MovieNamePoster(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string posterPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"Movie (2020)-poster.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(posterPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(posterPath, ArtworkKind.Poster);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_FanArt(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string fanArtPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"fanart.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(fanArtPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(fanArtPath, ArtworkKind.FanArt);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_FanArt_Backdrop(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string fanArtPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"backdrop.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(fanArtPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(fanArtPath, ArtworkKind.FanArt);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_And_MovieNameFanArt(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string fanArtPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"Movie (2020)-fanart.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(fanArtPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _imageCache.Received(1).CopyArtworkToCache(fanArtPath, ArtworkKind.FanArt);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_FanArt_Ignores_Folder_Image(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string folderImagePath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"folder.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(folderImagePath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            // Should receive Poster call (as folder.ext is valid for poster)
            await _imageCache.Received(1).CopyArtworkToCache(folderImagePath, ArtworkKind.Poster);

            // Should NOT receive FanArt call
            await _imageCache.DidNotReceive().CopyArtworkToCache(Arg.Any<string>(), ArtworkKind.FanArt);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_Poster_Priority(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string posterPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"poster.{imageExtension}");

            string moviePosterPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"Movie (2020)-poster.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(posterPath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(moviePosterPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            // Should prefer "poster.ext" over "MovieName-poster.ext"
            await _imageCache.Received(1).CopyArtworkToCache(posterPath, ArtworkKind.Poster);
            await _imageCache.DidNotReceive().CopyArtworkToCache(moviePosterPath, ArtworkKind.Poster);
        }

        [Test]
        public async Task NewMovie_Statistics_And_FallbackMetadata_FanArt_Priority(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ImageFileExtensions))]
            string imageExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            string fanArtPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"fanart.{imageExtension}");

            string movieFanArtPath = Path.Combine(
                Path.GetDirectoryName(moviePath) ?? string.Empty,
                $"Movie (2020)-fanart.{imageExtension}");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(fanArtPath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(movieFanArtPath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            // Should prefer "fanart.ext" over "MovieName-fanart.ext"
            await _imageCache.Received(1).CopyArtworkToCache(fanArtPath, ArtworkKind.FanArt);
            await _imageCache.DidNotReceive().CopyArtworkToCache(movieFanArtPath, ArtworkKind.FanArt);
        }

        [Test]
        public async Task Should_Ignore_Extra_Files(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ExtraFiles))]
            string extraFile)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(
                    Path.Combine(
                        Path.GetDirectoryName(moviePath) ?? string.Empty,
                        $"Movie (2020)-{extraFile}{videoExtension}"))
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task Should_Ignore_Dot_Underscore_Files(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(
                    Path.Combine(
                        Path.GetDirectoryName(moviePath) ?? string.Empty,
                        $"._Movie (2020){videoExtension}"))
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task Should_Ignore_Extra_Folders(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension,
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.ExtraDirectories))]
            string extraFolder)
        {
            string moviePath = Path.Combine(
                FakeRoot,
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now },
                new FakeFileEntry(
                    Path.Combine(
                        Path.GetDirectoryName(moviePath) ?? string.Empty,
                        Path.Combine(extraFolder, $"Movie (2020){videoExtension}")))
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task Should_Work_With_Nested_Folders(
            [ValueSource(typeof(LocalFolderScanner), nameof(LocalFolderScanner.VideoFileExtensions))]
            string videoExtension)
        {
            string moviePath = Path.Combine(
                Path.Combine(FakeRoot, "L-P"),
                Path.Combine("Movie (2020)", $"Movie (2020){videoExtension}"));

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _movieRepository.Received(1).GetOrAdd(
                Arg.Any<LibraryPath>(),
                Arg.Any<LibraryFolder>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await _movieRepository.Received(1).GetOrAdd(
                libraryPath,
                Arg.Any<LibraryFolder>(),
                moviePath,
                Arg.Any<CancellationToken>());

            await _localStatisticsProvider.Received(1).RefreshStatistics(
                FFmpegPath,
                FFprobePath,
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));

            await _localMetadataProvider.Received(1).RefreshFallbackMetadata(
                Arg.Is<Movie>(i => i != null && i.MediaVersions.Head().MediaFiles.Head().Path == moviePath));
        }

        [Test]
        public async Task RenamedMovie_Should_Delete_Old_Movie()
        {
            // TODO: handle this case more elegantly
            // ideally, detect that the movie was renamed and still delete the old one (or update the path?)

            string movieFolder = Path.Combine(FakeRoot, "Movie (2020)");
            string oldMoviePath = Path.Combine(movieFolder, "Movie (2020).avi");

            _movieRepository.FindMoviePaths(Arg.Any<LibraryPath>())
                .Returns(new List<string> { oldMoviePath }.AsEnumerable().AsTask());

            string moviePath = Path.Combine(movieFolder, "Movie (2020).mkv");

            MovieFolderScanner service = GetService(
                new FakeFileEntry(moviePath) { LastWriteTime = DateTime.Now }
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _mediaItemRepository.Received(1).FlagFileNotFound(Arg.Any<LibraryPath>(), Arg.Any<string>());
            await _mediaItemRepository.Received(1).FlagFileNotFound(libraryPath, oldMoviePath);
        }

        [Test]
        public async Task DeletedMovieAndFolder_Should_Flag_File_Not_Found()
        {
            string movieFolder = Path.Combine(FakeRoot, "Movie (2020)");
            string oldMoviePath = Path.Combine(movieFolder, "Movie (2020).avi");

            _movieRepository.FindMoviePaths(Arg.Any<LibraryPath>())
                .Returns(new List<string> { oldMoviePath }.AsEnumerable().AsTask());

            MovieFolderScanner service = GetService(
                new FakeFolderEntry(FakeRoot)
            );
            var libraryPath = new LibraryPath
                { Id = 1, Path = FakeRoot, LibraryFolders = new List<LibraryFolder>() };

            Either<BaseError, Unit> result = await service.ScanFolder(
                libraryPath,
                FFmpegPath,
                FFprobePath,
                0,
                1,
                CancellationToken.None);

            result.IsRight.ShouldBeTrue();

            await _mediaItemRepository.Received(1).FlagFileNotFound(Arg.Any<LibraryPath>(), Arg.Any<string>());
            await _mediaItemRepository.Received(1).FlagFileNotFound(libraryPath, oldMoviePath);
        }

        private MovieFolderScanner GetService(params FakeFileEntry[] files)
        {
            var fileSystem = new MockFileSystem();
            IFileSystemInitializer<MockFileSystem> init = fileSystem.Initialize();
            foreach (var file in files)
            {
                init.WithFile(file.Path).Which(f => f.File.LastWriteTime = file.LastWriteTime);
            }

            return new MovieFolderScanner(
                _scannerProxy,
                fileSystem,
                new LocalFileSystem(fileSystem, Substitute.For<ILogger<LocalFileSystem>>()),
                _movieRepository,
                _localStatisticsProvider,
                Substitute.For<ILocalSubtitlesProvider>(),
                Substitute.For<ILocalChaptersProvider>(),
                _localMetadataProvider,
                Substitute.For<IMetadataRepository>(),
                _imageCache,
                _libraryRepository,
                _mediaItemRepository,
                Substitute.For<IFFmpegPngService>(),
                Substitute.For<ITempFilePool>(),
                Logger);
        }

        private MovieFolderScanner GetService(params FakeFolderEntry[] folders)
        {
            var fileSystem = new MockFileSystem();
            IFileSystemInitializer<MockFileSystem> init = fileSystem.Initialize();
            foreach (var folder in folders)
            {
                init.WithSubdirectory(folder.Path);
            }

            return new MovieFolderScanner(
                _scannerProxy,
                fileSystem,
                new LocalFileSystem(fileSystem, Substitute.For<ILogger<LocalFileSystem>>()),
                _movieRepository,
                _localStatisticsProvider,
                Substitute.For<ILocalSubtitlesProvider>(),
                Substitute.For<ILocalChaptersProvider>(),
                _localMetadataProvider,
                Substitute.For<IMetadataRepository>(),
                _imageCache,
                _libraryRepository,
                _mediaItemRepository,
                Substitute.For<IFFmpegPngService>(),
                Substitute.For<ITempFilePool>(),
                Logger);
        }
    }
}
