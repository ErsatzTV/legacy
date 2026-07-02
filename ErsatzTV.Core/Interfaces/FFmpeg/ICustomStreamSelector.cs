using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;

namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface ICustomStreamSelector
{
    Task<StreamSelectorResult> SelectStreams(
        Channel channel,
        DateTimeOffset contentStartTime,
        MediaItemAudioVersion audioVersion,
        List<Subtitle> allSubtitles,
        bool shouldLogMessages);
}
