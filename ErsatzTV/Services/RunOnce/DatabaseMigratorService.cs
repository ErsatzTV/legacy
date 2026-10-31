using System.Reflection;
using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Services.RunOnce;

public class DatabaseMigratorService : BackgroundService
{
    private readonly ILogger<DatabaseMigratorService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SystemStartup _systemStartup;

    public DatabaseMigratorService(
        IServiceScopeFactory serviceScopeFactory,
        SystemStartup systemStartup,
        ILogger<DatabaseMigratorService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _systemStartup = systemStartup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        await using TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

        // extract empty database to speed up initial startup
        if (TvContext.IsSqlite && !File.Exists(FileSystemLayout.DatabasePath))
        {
            _logger.LogInformation("Extracting empty database to {DatabasePath}", FileSystemLayout.DatabasePath);
            await using Stream resource = typeof(ResourceExtractorService).GetTypeInfo().Assembly
                .GetManifestResourceStream("ErsatzTV.Resources.empty.sqlite3");
            if (resource != null)
            {
                await using FileStream fs = File.Create(FileSystemLayout.DatabasePath);
                await resource.CopyToAsync(fs, stoppingToken);
            }
        }

        _logger.LogInformation("Applying database migrations");

        if (TvContext.IsSqlite)
        {
            int count = await dbContext.Connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'");
            if (count > 0)
            {
                count = await dbContext.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM `__EFMigrationsLock`");
                if (count > 0)
                {
                    _logger.LogWarning(
                        "Cleaning database migrations lock; this is needed when ETV is terminated during a database migration.");

                    // sqlite migrations lock is always stale since mutex ensures single instance of etv
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM `__EFMigrationsLock`", stoppingToken);
                }
            }
        }

        List<string> pendingMigrations = await dbContext.Database
            .GetPendingMigrationsAsync(stoppingToken)
            .Map(l => l.ToList());

        if (pendingMigrations.Any(m => m.Contains("Add_MediaFilePathHash", StringComparison.OrdinalIgnoreCase)))
        {
            await dbContext.Database.MigrateAsync("Add_MediaFilePathHash", stoppingToken);
        }

        List<string> appliedMigrations = await dbContext.Database
            .GetAppliedMigrationsAsync(stoppingToken)
            .Map(l => l.ToList());

        if (appliedMigrations.Count > 0)
        {
            // this can't be part of a migration, so we have to stop here and run some sql
            await PopulatePathHashes(dbContext);
        }

        // then continue migrating
        await dbContext.Database.MigrateAsync(stoppingToken);

        _logger.LogInformation("Initializing database");
        await DbInitializer.Initialize(dbContext, stoppingToken);

        if (!LegacyStreamingEngine.Enabled)
        {
            await MoveLegacyChannelsToNext(dbContext, stoppingToken);
        }

        _systemStartup.DatabaseIsReady();

        _logger.LogInformation("Done applying database migrations");
    }

    // not an ef migration: a hotfix build with legacy enabled must not move channels
    private async Task MoveLegacyChannelsToNext(TvContext dbContext, CancellationToken cancellationToken)
    {
        // next only supports TransportStreamHybrid (5) and HttpLiveStreamingSegmenter (4)
        int count = await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE `Channel`
                SET `StreamingEngine` = 1,
                    `StreamingMode` = CASE `StreamingMode` WHEN 1 THEN 5 WHEN 2 THEN 4 ELSE `StreamingMode` END
                WHERE `StreamingEngine` = 0
                """,
                cancellationToken: cancellationToken));

        if (count > 0)
        {
            _logger.LogWarning(
                "The legacy streaming engine has been removed; moved {Count} channel(s) to the next streaming engine",
                count);
        }
    }

    private async Task PopulatePathHashes(TvContext dbContext)
    {
        if (await dbContext.Connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM `MediaFile` WHERE `PathHash` IS NULL OR `PathHash` = ''") == 0)
        {
            return;
        }

        _logger.LogInformation("Populating database path hashes");

        if (dbContext.Connection is SqliteConnection sqliteConnection)
        {
            sqliteConnection.CreateFunction("HASH_SHA256", (string text) => PathUtils.GetPathHash(text));
            await dbContext.Connection.ExecuteAsync("UPDATE `MediaFile` SET `PathHash` = HASH_SHA256(`Path`);");
        }
        else
        {
            // mysql
            await dbContext.Connection.ExecuteAsync("UPDATE `MediaFile` SET `PathHash` = sha2(`Path`, 256);");
        }
    }
}
