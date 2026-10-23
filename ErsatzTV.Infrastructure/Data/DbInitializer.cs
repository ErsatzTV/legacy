using System.Globalization;
using System.Reflection;
using Dapper;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Infrastructure.Data;

public static class DbInitializer
{
    public static async Task<Unit> Initialize(TvContext context, CancellationToken cancellationToken)
    {
        if (TvContext.IsSqlite)
        {
            await context.Connection.ExecuteAsync("PRAGMA journal_mode=WAL", cancellationToken);
        }
        else
        {
            int localInfile = await context.Connection.ExecuteScalarAsync<int>(
                "SELECT @@GLOBAL.local_infile");

            if (localInfile == 0)
            {
                try
                {
                    await context.Connection.ExecuteAsync("SET GLOBAL local_infile = 'ON'", cancellationToken);
                }
                catch
                {
                    Serilog.Log.Fatal(
                        """
                        ErsatzTV requires local_infile=ON when using MySQL.
                        Please run the following as a MySQL administrator:
                          SET GLOBAL local_infile = 'ON';
                        Or configure it in my.cnf under [mysqld].
                        """);
                    throw;
                }
            }
        }

        if (!context.LanguageCodes.Any())
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly != null)
            {
                await using Stream resource =
                    assembly.GetManifestResourceStream("ErsatzTV.Resources.ISO-639-2_utf-8.txt");
                if (resource != null)
                {
                    using var reader = new StreamReader(resource);
                    string line;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                    {
                        string[] split = line.Split("|");
                        if (split.Length == 5)
                        {
                            var languageCode = new LanguageCode
                            {
                                ThreeCode1 = split[0],
                                ThreeCode2 = split[1],
                                TwoCode = split[2],
                                EnglishName = split[3],
                                FrenchName = split[4]
                            };

                            await context.LanguageCodes.AddAsync(languageCode, cancellationToken);
                        }
                    }
                }
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        if (!context.PlaylistGroups.Any(pg => pg.IsSystem))
        {
            var pg = new PlaylistGroup
            {
                Name = "Trakt Lists",
                IsSystem = true
            };

            await context.PlaylistGroups.AddAsync(pg, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        if (context.Resolutions.Any(x => x.Width == 1920))
        {
            return Unit.Default;
        }

        var resolutions = new List<Resolution>
        {
            new() { Id = 1, Name = "720x480", Width = 720, Height = 480 },
            new() { Id = 2, Name = "1280x720", Width = 1280, Height = 720 },
            new() { Id = 3, Name = "1920x1080", Width = 1920, Height = 1080 },
            new() { Id = 4, Name = "3840x2160", Width = 3840, Height = 2160 }
        };
        await context.Resolutions.AddRangeAsync(resolutions, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var resolutionConfig = new ConfigElement
        {
            Key = ConfigElementKey.FFmpegDefaultResolutionId.Key,
            Value = "3" // 1920x1080
        };
        await context.ConfigElements.AddAsync(resolutionConfig, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var defaultProfile = FFmpegProfile.New("1920x1080 x264 aac", resolutions[2]);
        await context.FFmpegProfiles.AddAsync(defaultProfile, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var profileConfig = new ConfigElement
        {
            Key = ConfigElementKey.FFmpegDefaultProfileId.Key,
            Value = defaultProfile.Id.ToString(CultureInfo.InvariantCulture)
        };
        await context.ConfigElements.AddAsync(profileConfig, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var defaultChannel = new Channel(Guid.NewGuid())
        {
            Number = "1",
            Name = "ErsatzTV",
            FFmpegProfile = defaultProfile,
            StreamingEngine = StreamingEngine.Next,
            StreamingMode = StreamingMode.TransportStreamHybrid,
            IsEnabled = true,
            ShowInEpg = true
        };
        await context.Channels.AddAsync(defaultChannel, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // TODO: create looping static image that mentions configuring via web
        return Unit.Default;
    }
}
