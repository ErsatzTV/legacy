using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.FFmpeg;

public static class MusicVideoCreditsSubtitle
{
    // troubleshooting playout items never reach the database, so the credits endpoint
    // resolves this id from the troubleshooting playout item store instead
    public const int TroubleshootingPlayoutItemId = 0;

    public static Subtitle ForPlayoutItem(int playoutItemId, TimeSpan inPoint)
    {
        string seekToMs = inPoint > TimeSpan.Zero
            ? $"?seekToMs={(long)inPoint.TotalMilliseconds}"
            : string.Empty;

        return new Subtitle
        {
            Codec = "ass",
            Default = true,
            Forced = true,
            IsExtracted = false,
            SubtitleKind = SubtitleKind.Generated,
            Path = $"http://localhost:{Settings.StreamingPort}/internal/ffmpeg/music-video-credits/{playoutItemId}{seekToMs}",
            SDH = false
        };
    }
}
