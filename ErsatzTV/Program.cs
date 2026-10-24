using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Destructurama;
using ErsatzTV.Core;
using ErsatzTV.Services.Validators;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace ErsatzTV;

public class Program
{
    private static readonly string BasePath;

    static Program()
    {
        string executablePath = Environment.ProcessPath ?? string.Empty;
        string executable = Path.GetFileNameWithoutExtension(executablePath);

        IConfigurationBuilder builder = new ConfigurationBuilder();

        BasePath = Path.GetDirectoryName(
            "dotnet".Equals(executable, StringComparison.OrdinalIgnoreCase)
                ? typeof(Program).Assembly.Location
                : executablePath);

        Configuration = builder
            .SetBasePath(BasePath)
            .AddJsonFile("appsettings.json", false, false)
            .AddJsonFile(
                $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
                true)
            .AddEnvironmentVariables()
            .Build();

        LoggingLevelSwitches = new LoggingLevelSwitches();
        InMemoryLogService = new InMemoryLogService();
    }

    private static IConfiguration Configuration { get; }

    private static LoggingLevelSwitches LoggingLevelSwitches { get; }

    internal static InMemoryLogService InMemoryLogService { get; }

    public static async Task<int> Main(string[] args)
    {
        // A lock file in the config folder instead of a named mutex: since .NET 10.0.13 named mutexes
        // need a writable home directory, and a file lock also covers containers sharing one config folder.
        using FileStream singletonLock = TryLockConfigFolder(out bool alreadyRunning);
        if (alreadyRunning)
        {
            Console.WriteLine("Another instance of ErsatzTV is already running with this config folder.");
            return 1;
        }

        LoggingLevelSwitches.DefaultLevelSwitch.MinimumLevel = LogEventLevel.Information;
        LoggingLevelSwitches.ScanningLevelSwitch.MinimumLevel = LogEventLevel.Information;
        LoggingLevelSwitches.SchedulingLevelSwitch.MinimumLevel = LogEventLevel.Information;
        LoggingLevelSwitches.StreamingLevelSwitch.MinimumLevel = LogEventLevel.Information;
        LoggingLevelSwitches.HttpLevelSwitch.MinimumLevel = LogEventLevel.Information;

        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(Configuration)
            .MinimumLevel.ControlledBy(LoggingLevelSwitches.DefaultLevelSwitch)

            // scanning
            .MinimumLevel.Override("ErsatzTV.Services.ScannerService", LoggingLevelSwitches.ScanningLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Services.SearchIndexService", LoggingLevelSwitches.ScanningLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Scanner", LoggingLevelSwitches.ScanningLevelSwitch)

            // scheduling
            .MinimumLevel.Override("ErsatzTV.Core.Scheduling", LoggingLevelSwitches.SchedulingLevelSwitch)
            .MinimumLevel.Override(
                "ErsatzTV.Application.Subtitles.ExtractEmbeddedSubtitlesHandler",
                LoggingLevelSwitches.SchedulingLevelSwitch)

            // searching
            .MinimumLevel.Override(
                "ErsatzTV.Infrastructure.Search.SearchQueryParser",
                LoggingLevelSwitches.SearchingLevelSwitch)

            // streaming
            .MinimumLevel.Override("ErsatzTV.Application.Streaming", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Application.Troubleshooting", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.FFmpeg", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Core.FFmpeg", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Controllers.IptvController", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override("ErsatzTV.Controllers.InternalController", LoggingLevelSwitches.StreamingLevelSwitch)
            .MinimumLevel.Override(
                "ErsatzTV.Controllers.TroubleshootController",
                LoggingLevelSwitches.StreamingLevelSwitch)

            // http
            .MinimumLevel.Override("Serilog.AspNetCore.RequestLoggingMiddleware", LoggingLevelSwitches.HttpLevelSwitch)

            // api key handler logs debug on every authenticated api request
            .MinimumLevel.Override("ErsatzTV.Security", LogEventLevel.Warning)
            .Destructure.UsingAttributes()
            .Enrich.FromLogContext()
            .WriteTo.Sink(InMemoryLogService.Sink)
            .WriteTo.File(
                FileSystemLayout.LogFilePath,
                rollingInterval: RollingInterval.Day,
                formatProvider: CultureInfo.InvariantCulture);

        // for performance reasons, restrict windows console to error logs
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Debugger.IsAttached)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console(
                LogEventLevel.Error,
                theme: AnsiConsoleTheme.Code,
                formatProvider: CultureInfo.InvariantCulture);
        }
        else
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                formatProvider: CultureInfo.InvariantCulture);

            // for troubleshooting log category
            // outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{SourceContext:l}> {NewLine}{Exception}"
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");

            IHost host = CreateHostBuilder(args).Build();

            //HibernatingRhinos.Profiler.Appender.EntityFramework.EntityFrameworkProfiler.Initialize();

            // run environment validation and exit on failure
            var validator = host.Services.GetRequiredService<IEnvironmentValidator>();
            if (!await validator.Validate())
            {
                return 1;
            }

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static FileStream TryLockConfigFolder(out bool alreadyRunning)
    {
        alreadyRunning = false;

        try
        {
            Directory.CreateDirectory(FileSystemLayout.AppDataFolder);

            // Read access still takes an exclusive lock, and works when the file is owned by another uid
            // (e.g. a container that previously ran as root) or the config folder is read-only.
            return new FileStream(
                Path.Combine(FileSystemLayout.AppDataFolder, "ersatztv.lock"),
                FileMode.OpenOrCreate,
                FileAccess.Read,
                FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            alreadyRunning = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Unable to lock config folder; continuing without single instance check: {ex.Message}");
        }

        return null;
    }

    // Unix reports the raw errno for EWOULDBLOCK instead of a Windows HRESULT
    private static bool IsSharingViolation(IOException ex) =>
        OperatingSystem.IsWindows()
            ? ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : ex.HResult == (OperatingSystem.IsLinux() ? 11 : 35);

    private static IHostBuilder CreateHostBuilder(string[] args)
    {
        Settings.UiPort = SystemEnvironment.UiPort;
        Settings.StreamingPort = SystemEnvironment.StreamingPort;

        return Host.CreateDefaultBuilder(args)
            .ConfigureServices(services => services.AddSingleton(LoggingLevelSwitches))
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>()
                .UseConfiguration(Configuration)
                .UseKestrel(options =>
                {
                    options.ListenAnyIP(Settings.UiPort);

                    if (Settings.StreamingPort != Settings.UiPort)
                    {
                        options.ListenAnyIP(Settings.StreamingPort);
                    }

                    options.AddServerHeader = false;
                })
                .UseContentRoot(BasePath))
            .UseSerilog();
    }
}
