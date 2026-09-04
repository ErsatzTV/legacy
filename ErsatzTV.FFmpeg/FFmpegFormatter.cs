using System.Globalization;

namespace ErsatzTV.FFmpeg;

public static class FFmpegFormatter
{
    public static string Milliseconds(TimeSpan timeSpan) =>
        ((long)timeSpan.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
}
