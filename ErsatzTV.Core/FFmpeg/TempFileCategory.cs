namespace ErsatzTV.Core.FFmpeg;

public enum TempFileCategory
{
    Subtitle = 0,
    SongBackground = 1,
    CoverArt = 2,
    CachedArtwork = 3,
    MpegTsScript = 4,

    // server only; scanner pool uses the same folder
    TroubleshootingUpload = 96,

    Fmp4LastSegment = 97,
    BadTranscodeFolder = 98,
    BadPlaylist = 99
}
