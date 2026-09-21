using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ErsatzTV.FFmpeg.Format;

namespace ErsatzTV.FFmpeg;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public record MediaStream(int Index, string Codec, StreamKind Kind);

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public record AudioStream(int Index, string Codec, int Channels) : MediaStream(
    Index,
    Codec,
    StreamKind.Audio);

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public record VideoStream(
    int Index,
    string Codec,
    string Profile,
    Option<IPixelFormat> PixelFormat,
    ColorParams ColorParams,
    FrameSize FrameSize,
    string MaybeSampleAspectRatio,
    string DisplayAspectRatio,
    Option<FrameRate> FrameRate,
    bool StillImage,
    ScanKind ScanKind) : MediaStream(
    Index,
    Codec,
    StreamKind.Video)
{
    public ColorParams ColorParams { get; private set; } = ColorParams;

    public bool HasMultipleProfiles { get; set; }

    // rotation (0, 90, 180, 270) from display matrix side data; FrameSize is already swapped for 90/270
    public int Rotation { get; init; }

    public bool IsRotatedQuarterTurn => Rotation is 90 or 270;

    public int BitDepth => PixelFormat.Map(pf => pf.BitDepth).IfNone(8);

    public string SampleAspectRatio
    {
        get
        {
            // some media servers don't provide sample aspect ratio so we have to calculate it
            if (string.IsNullOrWhiteSpace(MaybeSampleAspectRatio) || MaybeSampleAspectRatio == "0:0")
            {
                // first check for decimal DAR
                if (!double.TryParse(DisplayAspectRatio, out double dar))
                {
                    // if not, assume it's a ratio
                    string[] split = DisplayAspectRatio.Split(':');
                    var num = double.Parse(split[0], CultureInfo.InvariantCulture);
                    var den = double.Parse(split[1], CultureInfo.InvariantCulture);
                    dar = num / den;
                }

                double res = FrameSize.Width / (double)FrameSize.Height;
                var formattedDar = string.Format(CultureInfo.InvariantCulture, dar % 1 == 0 ? "{0:F0}" : "{0:0.############}", dar);
                var formattedRes = string.Format(CultureInfo.InvariantCulture, res % 1 == 0 ? "{0:F0}" : "{0:0.############}", res);
                return $"{formattedDar}:{formattedRes}";
            }

            {
                string[] split = MaybeSampleAspectRatio.Split(':');
                var num = double.Parse(split[0], CultureInfo.InvariantCulture);
                var den = double.Parse(split[1], CultureInfo.InvariantCulture);
                var formattedNum = string.Format(CultureInfo.InvariantCulture, num % 1 == 0 ? "{0:F0}" : "{0:0.############}", num);
                var formattedDen = string.Format(CultureInfo.InvariantCulture, den % 1 == 0 ? "{0:F0}" : "{0:0.############}", den);
                return $"{formattedNum}:{formattedDen}";
            }
        }
    }

    public bool IsAnamorphic
    {
        get
        {
            // square pixels
            if (SampleAspectRatio == "1:1")
            {
                return false;
            }

            // 0:1 is "unspecified", so anything other than that will be non-square/anamorphic
            if (SampleAspectRatio != "0:1")
            {
                return true;
            }

            // SAR 0:1 && DAR 0:1 (both unspecified) means square
            if (DisplayAspectRatio == "0:1")
            {
                return false;
            }

            // DAR == W:H is square
            return DisplayAspectRatio != $"{FrameSize.Width}:{FrameSize.Height}";
        }
    }

    // TODO: figure out what's really causing this, then re-enable if needed
    public static bool IsAnamorphicEdgeCase => false;

    public FrameSize SquarePixelFrameSize(FrameSize resolution)
    {
        int width = FrameSize.Width;
        int height = FrameSize.Height;

        if (IsAnamorphic)
        {
            double sar = GetSAR();
            bool edgeCase = IsAnamorphicEdgeCase;

            width = edgeCase
                ? FrameSize.Width
                : (int)Math.Floor(FrameSize.Width * sar);

            height = edgeCase
                ? (int)Math.Floor(FrameSize.Height * sar)
                : FrameSize.Height;
        }

        double widthPercent = (double)resolution.Width / width;
        double heightPercent = (double)resolution.Height / height;
        double minPercent = Math.Min(widthPercent, heightPercent);

        var result = new FrameSize(
            (int)Math.Floor(width * minPercent),
            (int)Math.Floor(height * minPercent));

        return result;
    }

    public FrameSize SquarePixelFrameSizeForCrop(FrameSize resolution)
    {
        int width = FrameSize.Width;
        int height = FrameSize.Height;

        if (IsAnamorphic)
        {
            double sar = GetSAR();
            bool edgeCase = IsAnamorphicEdgeCase;

            width = edgeCase
                ? FrameSize.Width
                : (int)Math.Floor(FrameSize.Width * sar);

            height = edgeCase
                ? (int)Math.Floor(FrameSize.Height * sar)
                : FrameSize.Height;
        }

        double widthPercent = (double)resolution.Width / width;
        double heightPercent = (double)resolution.Height / height;
        double maxPercent = Math.Max(widthPercent, heightPercent);

        var result = new FrameSize(
            (int)Math.Floor(width * maxPercent),
            (int)Math.Floor(height * maxPercent));

        return result;
    }

    public void ResetColorParams(ColorParams colorParams) => ColorParams = colorParams;

    private double GetSAR()
    {
        // some media servers don't provide sample aspect ratio so we have to calculate it
        if (string.IsNullOrWhiteSpace(MaybeSampleAspectRatio) || MaybeSampleAspectRatio == "0:0")
        {
            // first check for decimal DAR
            if (!double.TryParse(DisplayAspectRatio, out double dar))
            {
                // if not, assume it's a ratio
                string[] split = DisplayAspectRatio.Split(':');
                var num = double.Parse(split[0], CultureInfo.InvariantCulture);
                var den = double.Parse(split[1], CultureInfo.InvariantCulture);
                dar = num / den;
            }

            double res = FrameSize.Width / (double)FrameSize.Height;
            return dar / res;
        }

        {
            string[] split = MaybeSampleAspectRatio.Split(':');
            var num = double.Parse(split[0], CultureInfo.InvariantCulture);
            var den = double.Parse(split[1], CultureInfo.InvariantCulture);
            return num / den;
        }
    }
}
