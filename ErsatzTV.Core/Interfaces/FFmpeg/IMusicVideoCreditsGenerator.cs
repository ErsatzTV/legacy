using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;

namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface IMusicVideoCreditsGenerator
{
    Task<Option<Subtitle>> GenerateCreditsSubtitle(MusicVideo musicVideo, FFmpegProfile ffmpegProfile);

    Task<Option<Subtitle>> GenerateCreditsSubtitleFromTemplate(
        MusicVideo musicVideo,
        FFmpegProfile ffmpegProfile,
        Option<TimeSpan> streamSeek,
        string templateFileName);
}
