using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Services;

public class FFmpegLocatorService : BackgroundService
{
    private readonly ILogger<FFmpegLocatorService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SystemStartup _systemStartup;

    public FFmpegLocatorService(
        IServiceScopeFactory serviceScopeFactory,
        SystemStartup systemStartup,
        ILogger<FFmpegLocatorService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _systemStartup = systemStartup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await _systemStartup.WaitForDatabase(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IFFmpegLocator ffmpegLocator = scope.ServiceProvider.GetRequiredService<IFFmpegLocator>();

        if (OperatingSystem.IsMacOS())
        {
            IConfigElementRepository configElementRepository =
                scope.ServiceProvider.GetRequiredService<IConfigElementRepository>();
            await AdoptBundledFFmpeg(configElementRepository, stoppingToken);
        }

        // check for ffmpeg and ffprobe in the last known/configured location
        // otherwise search using which/where and save any located executables
        Option<string> maybeFFmpegPath = await ffmpegLocator.ValidatePath(
            "ffmpeg",
            ConfigElementKey.FFmpegPath,
            stoppingToken);
        maybeFFmpegPath.Match(
            path => _logger.LogInformation("Located ffmpeg at {Path}", path),
            () => _logger.LogWarning("Failed to locate ffmpeg executable"));

        Option<string> maybeFFprobePath =
            await ffmpegLocator.ValidatePath("ffprobe", ConfigElementKey.FFprobePath, stoppingToken);
        maybeFFprobePath.Match(
            path => _logger.LogInformation("Located ffprobe at {Path}", path),
            () => _logger.LogWarning("Failed to locate ffprobe executable"));
    }

    // saved paths on macOS were usually found with `which` (homebrew) and would beat the bundled ffmpeg,
    // so replace them once; the marker is only written when the bundle exists, and later user changes stick
    private async Task AdoptBundledFFmpeg(
        IConfigElementRepository configElementRepository,
        CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        string ffmpegPath = Path.Combine(folder, "ffmpeg");
        string ffprobePath = Path.Combine(folder, "ffprobe");
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
        {
            return;
        }

        Option<bool> maybeAdopted = await configElementRepository.GetValue<bool>(
            ConfigElementKey.FFmpegMacOSBundledAdopted,
            cancellationToken);
        if (maybeAdopted.IfNone(false))
        {
            return;
        }

        foreach ((ConfigElementKey key, string path) in new[]
                 {
                     (ConfigElementKey.FFmpegPath, ffmpegPath),
                     (ConfigElementKey.FFprobePath, ffprobePath)
                 })
        {
            Option<string> maybeExisting = await configElementRepository.GetValue<string>(key, cancellationToken);
            foreach (string existing in maybeExisting.Filter(existing => existing != path))
            {
                _logger.LogWarning(
                    "Replacing configured path {OldPath} with bundled {NewPath}",
                    existing,
                    path);
            }

            await configElementRepository.Upsert(key, path, cancellationToken);
        }

        await configElementRepository.Upsert(ConfigElementKey.FFmpegMacOSBundledAdopted, true, cancellationToken);
    }
}
