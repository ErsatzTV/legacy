using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Metadata;

[TestFixture]
public class BackfillDateAddedTests
{
    [SetUp]
    public async Task SetUp()
    {
        _dbFileName = Path.Combine(Path.GetTempPath(), $"etv-backfill-{Guid.NewGuid():N}.sqlite3");
        DbContextOptions<TvContext> options = new DbContextOptionsBuilder<TvContext>()
            .UseSqlite(
                $"Data Source={_dbFileName};foreign keys=true;Pooling=false;",
                o => o.MigrationsAssembly("ErsatzTV.Infrastructure.Sqlite"))
            .Options;
        _factory = new TestDbContextFactory(options);

        await using TvContext dbContext = await _factory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown() => File.Delete(_dbFileName);

    private string _dbFileName;
    private IDbContextFactory<TvContext> _factory;

    [Test]
    public async Task BackfillDateAdded_Should_Only_Update_Missing_Dates_In_Library_Path()
    {
        var existingDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var creationDate = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        int libraryPathId, otherLibraryPathId, missingId, presentId, otherPathMissingId;
        await using (TvContext dbContext = await _factory.CreateDbContextAsync())
        {
            var libraryPath = new LibraryPath { Path = "/media" };
            var otherLibraryPath = new LibraryPath { Path = "/other" };
            dbContext.LocalMediaSources.Add(
                new LocalMediaSource
                {
                    Libraries =
                    [
                        new LocalLibrary
                        {
                            Name = "Other Videos",
                            MediaKind = LibraryMediaKind.OtherVideos,
                            Paths = [libraryPath, otherLibraryPath]
                        }
                    ]
                });
            await dbContext.SaveChangesAsync();

            OtherVideo missing = NewOtherVideo(libraryPath, "/media/missing.mkv", default);
            OtherVideo present = NewOtherVideo(libraryPath, "/media/present.mkv", existingDate);
            OtherVideo otherPathMissing = NewOtherVideo(otherLibraryPath, "/other/missing.mkv", default);
            dbContext.OtherVideos.AddRange(missing, present, otherPathMissing);
            await dbContext.SaveChangesAsync();

            (libraryPathId, otherLibraryPathId) = (libraryPath.Id, otherLibraryPath.Id);
            (missingId, presentId, otherPathMissingId) = (missing.Id, present.Id, otherPathMissing.Id);
        }

        var repository = new MetadataRepository(_factory);
        var requestedPaths = new List<string>();

        List<int> result = await repository.BackfillDateAdded(
            libraryPathId,
            path =>
            {
                requestedPaths.Add(path);
                return creationDate;
            },
            CancellationToken.None);

        result.ShouldBe([missingId]);
        requestedPaths.ShouldBe(["/media/missing.mkv"]);

        await using (TvContext dbContext = await _factory.CreateDbContextAsync())
        {
            Dictionary<int, DateTime> dates = await dbContext.OtherVideoMetadata
                .ToDictionaryAsync(m => m.OtherVideoId, m => m.DateAdded);

            dates[missingId].ShouldBe(creationDate);
            dates[presentId].ShouldBe(existingDate);
            dates[otherPathMissingId].ShouldBe(SystemTime.MinValueUtc);
        }

        // second pass is a no-op
        (await repository.BackfillDateAdded(libraryPathId, _ => creationDate, CancellationToken.None))
            .ShouldBeEmpty();

        // a missing file leaves the date alone
        (await repository.BackfillDateAdded(otherLibraryPathId, _ => None, CancellationToken.None))
            .ShouldBeEmpty();
    }

    private static OtherVideo NewOtherVideo(LibraryPath libraryPath, string path, DateTime dateAdded) =>
        new()
        {
            LibraryPathId = libraryPath.Id,
            MediaVersions =
            [
                new MediaVersion
                {
                    MediaFiles = [new MediaFile { Path = path, PathHash = PathUtils.GetPathHash(path) }],
                    Streams = []
                }
            ],
            OtherVideoMetadata =
            [
                new OtherVideoMetadata
                {
                    MetadataKind = MetadataKind.Fallback,
                    Title = Path.GetFileNameWithoutExtension(path),
                    DateAdded = dateAdded
                }
            ],
            TraktListItems = []
        };

    private sealed class TestDbContextFactory(DbContextOptions<TvContext> options) : IDbContextFactory<TvContext>
    {
        public TvContext CreateDbContext() =>
            new(options, NullLoggerFactory.Instance, new SlowQueryInterceptor(NullLogger<SlowQueryInterceptor>.Instance));
    }
}
