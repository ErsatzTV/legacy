using ErsatzTV.FFmpeg.Format;

namespace ErsatzTV.FFmpeg.Filter;

public class TonemapFilter : BaseFilter
{
    private readonly FrameState _currentState;
    private readonly IPixelFormat _desiredPixelFormat;
    private readonly FFmpegState _ffmpegState;

    public TonemapFilter(FFmpegState ffmpegState, FrameState currentState, IPixelFormat desiredPixelFormat)
    {
        _ffmpegState = ffmpegState;
        _currentState = currentState;
        _desiredPixelFormat = desiredPixelFormat;
    }

    public override string Filter
    {
        get
        {
            // convert to linear light with a nominal peak, tonemap, then convert primaries and matrix
            // from bt2020 to bt709 - without the primaries/matrix conversion the SDR output is washed out
            var tonemap =
                $"zscale=transfer=linear:npl=100,format=gbrpf32le,zscale=primaries=bt709,tonemap={_ffmpegState.TonemapAlgorithm}:desat=0,zscale=transfer=bt709:matrix=bt709:range=tv,format={_desiredPixelFormat.FFmpegName}";

            if (_currentState.FrameDataLocation == FrameDataLocation.Hardware)
            {
                foreach (IPixelFormat pixelFormat in _currentState.PixelFormat)
                {
                    if (pixelFormat is PixelFormatCuda or PixelFormatVaapi)
                    {
                        foreach (IPixelFormat pf in AvailablePixelFormats.ForPixelFormat(pixelFormat.Name, null))
                        {
                            return $"hwdownload,format={pf.FFmpegName},{tonemap}";
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(pixelFormat.FFmpegName))
                    {
                        return $"hwdownload,format={pixelFormat.FFmpegName},{tonemap}";
                    }
                }

                return $"hwdownload,{tonemap}";
            }

            return tonemap;
        }
    }

    public override FrameState NextState(FrameState currentState) =>
        currentState with
        {
            PixelFormat = Some(_desiredPixelFormat),
            FrameDataLocation = FrameDataLocation.Software
        };
}
