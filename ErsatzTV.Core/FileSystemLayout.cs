using System.Reflection;
using Serilog;

namespace ErsatzTV.Core;

public static class FileSystemLayout
{
    public static readonly string AppDataFolder;

    public static readonly string TranscodeFolder;
    public static readonly string TranscodeTroubleshootingChannel;
    public static readonly string TranscodeTroubleshootingFolder;
    public static readonly string TranscodeTroubleshootingPlayoutFolder;

    public static readonly string DataProtectionFolder;
    public static readonly string LogsFolder;

    public static readonly string DatabasePath;
    public static readonly string LogFilePath;

    public static readonly string LegacyImageCacheFolder;
    public static readonly string ResourcesCacheFolder;
    public static readonly string ChannelGuideCacheFolder;

    public static readonly string PlexSecretsPath;
    public static readonly string JellyfinSecretsPath;
    public static readonly string EmbySecretsPath;

    public static readonly string FFmpegReportsFolder;
    public static readonly string SearchIndexFolder;
    public static readonly string TempFilePoolFolder;

    public static readonly string ArtworkCacheFolder;

    public static readonly string PosterCacheFolder;
    public static readonly string ThumbnailCacheFolder;
    public static readonly string LogoCacheFolder;
    public static readonly string FanArtCacheFolder;
    public static readonly string WatermarkCacheFolder;

    public static readonly string StreamsCacheFolder;

    public static readonly string SubtitleCacheFolder;
    public static readonly string FontsCacheFolder;

    public static readonly string TemplatesFolder;

    public static readonly string MusicVideoCreditsTemplatesFolder;

    public static readonly string ChannelGuideTemplatesFolder;

    public static readonly string GraphicsElementsTemplatesFolder;
    public static readonly string GraphicsElementsTextTemplatesFolder;
    public static readonly string GraphicsElementsImageTemplatesFolder;
    public static readonly string GraphicsElementsScriptTemplatesFolder;
    public static readonly string GraphicsElementsSubtitleTemplatesFolder;
    public static readonly string GraphicsElementsMotionTemplatesFolder;

    public static readonly string ScriptsFolder;

    public static readonly string MultiEpisodeShuffleTemplatesFolder;

    public static readonly string AudioStreamSelectorScriptsFolder;

    public static readonly string ChannelStreamSelectorsFolder;

    public static readonly string MpegTsScriptsFolder;

    public static readonly string DefaultMpegTsScriptFolder;

    public static readonly string NextChannelConfigOverlaysFolder;
    public static readonly string NextPlayoutsFolder;

    public static readonly string MacOsOldAppDataFolder = Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? string.Empty,
        ".local",
        "share",
        "ersatztv");

    public static readonly string MacOsOldDatabasePath = Path.Combine(MacOsOldAppDataFolder, "ersatztv.sqlite3");

    static FileSystemLayout()
    {
        string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        bool isDocker = version.Contains("docker", StringComparison.OrdinalIgnoreCase);

        string defaultConfigFolder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "ersatztv");

        string customConfigFolder = SystemEnvironment.ConfigFolder;
        bool useCustomConfigFolder = !string.IsNullOrWhiteSpace(customConfigFolder);

        if (useCustomConfigFolder && isDocker)
        {
            // check for config at old location
            if (Directory.Exists(defaultConfigFolder))
            {
                Log.Logger.Warning(
                    "Ignoring ETV_CONFIG_FOLDER {Folder} and using default {Default}",
                    customConfigFolder,
                    defaultConfigFolder);

                // ignore custom config folder
                useCustomConfigFolder = false;
            }
        }

        AppDataFolder = useCustomConfigFolder ? customConfigFolder : defaultConfigFolder;

        string defaultTranscodeFolder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "etv-transcode");

        string customTranscodeFolder = SystemEnvironment.TranscodeFolder;
        bool useCustomTranscodeFolder = !string.IsNullOrWhiteSpace(customTranscodeFolder);

        if (useCustomTranscodeFolder && isDocker)
        {
            // check for config at old location
            if (Directory.Exists(defaultTranscodeFolder))
            {
                Log.Logger.Warning(
                    "Ignoring ETV_TRANSCODE_FOLDER {Folder} and using default {Default}",
                    customTranscodeFolder,
                    defaultTranscodeFolder);

                // ignore custom config folder
                useCustomTranscodeFolder = false;
            }
        }

        TranscodeFolder = useCustomTranscodeFolder ? customTranscodeFolder : defaultTranscodeFolder;
        TranscodeTroubleshootingChannel = ".troubleshooting";
        TranscodeTroubleshootingFolder = Path.Combine(TranscodeFolder, TranscodeTroubleshootingChannel);

        DataProtectionFolder = Path.Combine(AppDataFolder, "data-protection");
        LogsFolder = Path.Combine(AppDataFolder, "logs");

        DatabasePath = Path.Combine(AppDataFolder, "ersatztv.sqlite3");
        LogFilePath = Path.Combine(LogsFolder, "ersatztv.log");

        LegacyImageCacheFolder = Path.Combine(AppDataFolder, "cache", "images");
        ResourcesCacheFolder = Path.Combine(AppDataFolder, "cache", "resources");
        ChannelGuideCacheFolder = Path.Combine(AppDataFolder, "cache", "channel-guide");

        PlexSecretsPath = Path.Combine(AppDataFolder, "plex-secrets.json");
        JellyfinSecretsPath = Path.Combine(AppDataFolder, "jellyfin-secrets.json");
        EmbySecretsPath = Path.Combine(AppDataFolder, "emby-secrets.json");

        FFmpegReportsFolder = Path.Combine(AppDataFolder, "ffmpeg-reports");
        SearchIndexFolder = Path.Combine(AppDataFolder, "search-index");
        TempFilePoolFolder = Path.Combine(AppDataFolder, "temp-pool");

        ArtworkCacheFolder = Path.Combine(AppDataFolder, "cache", "artwork");

        ArtworkCacheFolder = Path.Combine(AppDataFolder, "cache", "artwork");

        PosterCacheFolder = Path.Combine(ArtworkCacheFolder, "posters");
        ThumbnailCacheFolder = Path.Combine(ArtworkCacheFolder, "thumbnails");
        LogoCacheFolder = Path.Combine(ArtworkCacheFolder, "logos");
        FanArtCacheFolder = Path.Combine(ArtworkCacheFolder, "fanart");
        WatermarkCacheFolder = Path.Combine(ArtworkCacheFolder, "watermarks");

        StreamsCacheFolder = Path.Combine(AppDataFolder, "cache", "streams");

        SubtitleCacheFolder = Path.Combine(StreamsCacheFolder, "subtitles");
        FontsCacheFolder = Path.Combine(StreamsCacheFolder, "fonts");

        TemplatesFolder = Path.Combine(AppDataFolder, "templates");

        MusicVideoCreditsTemplatesFolder = Path.Combine(TemplatesFolder, "music-video-credits");

        ChannelGuideTemplatesFolder = Path.Combine(TemplatesFolder, "channel-guide");

        GraphicsElementsTemplatesFolder = Path.Combine(TemplatesFolder, "graphics-elements");
        GraphicsElementsTextTemplatesFolder = Path.Combine(GraphicsElementsTemplatesFolder, "text");
        GraphicsElementsImageTemplatesFolder = Path.Combine(GraphicsElementsTemplatesFolder, "image");
        GraphicsElementsScriptTemplatesFolder = Path.Combine(GraphicsElementsTemplatesFolder, "script");
        GraphicsElementsSubtitleTemplatesFolder = Path.Combine(GraphicsElementsTemplatesFolder, "subtitle");
        GraphicsElementsMotionTemplatesFolder = Path.Combine(GraphicsElementsTemplatesFolder, "motion");

        ScriptsFolder = Path.Combine(AppDataFolder, "scripts");

        MultiEpisodeShuffleTemplatesFolder = Path.Combine(ScriptsFolder, "multi-episode-shuffle");

        AudioStreamSelectorScriptsFolder = Path.Combine(ScriptsFolder, "audio-stream-selector");

        ChannelStreamSelectorsFolder = Path.Combine(ScriptsFolder, "channel-stream-selectors");

        MpegTsScriptsFolder = Path.Combine(ScriptsFolder, "mpegts");
        DefaultMpegTsScriptFolder = Path.Combine(MpegTsScriptsFolder, "default");

        NextChannelConfigOverlaysFolder = Path.Combine(AppDataFolder, "next", "channel-config-overlays");
        NextPlayoutsFolder = Path.Combine(AppDataFolder, "next", "playouts");
        TranscodeTroubleshootingPlayoutFolder = Path.Combine(NextPlayoutsFolder, TranscodeTroubleshootingChannel);
    }
}
