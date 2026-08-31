using ErsatzTV.FFmpeg.OutputFormat;

namespace ErsatzTV.Core.FFmpeg;

public interface IHlsPlaylistFilter
{
    Option<TrimPlaylistResult> TrimPlaylist(
        Dictionary<long, int> discontinuityMap,
        OutputFormatKind outputFormat,
        DateTimeOffset playlistStart,
        DateTimeOffset filterBefore,
        IHlsInitSegmentCache hlsInitSegmentCache,
        string[] lines,
        Option<int> maybeMaxSegments,
        bool endWithDiscontinuity = false);

    Option<TrimPlaylistResult> TrimPlaylistWithDiscontinuity(
        Dictionary<long, int> discontinuityMap,
        OutputFormatKind outputFormat,
        DateTimeOffset playlistStart,
        DateTimeOffset filterBefore,
        IHlsInitSegmentCache hlsInitSegmentCache,
        string[] lines);
}
