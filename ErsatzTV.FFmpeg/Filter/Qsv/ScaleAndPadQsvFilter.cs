using ErsatzTV.FFmpeg.Format;

namespace ErsatzTV.FFmpeg.Filter.Qsv;

public class ScaleAndPadQsvFilter : BaseFilter
{
    private readonly FrameState _currentState;
    private readonly int _extraHardwareFrames;
    private readonly bool _isAnamorphicEdgeCase;
    private readonly FrameSize _paddedSize;
    private readonly string _sampleAspectRatio;
    private readonly FrameSize _scaledSize;

    public ScaleAndPadQsvFilter(
        FrameState currentState,
        FrameSize scaledSize,
        FrameSize paddedSize,
        int extraHardwareFrames,
        bool isAnamorphicEdgeCase,
        string sampleAspectRatio)
    {
        _currentState = currentState;
        _scaledSize = scaledSize;
        _paddedSize = paddedSize;
        _extraHardwareFrames = extraHardwareFrames;
        _isAnamorphicEdgeCase = isAnamorphicEdgeCase;
        _sampleAspectRatio = sampleAspectRatio;
    }

    public override string Filter
    {
        get
        {
            // fold scale and pad into a single vpp_qsv instance; chaining two vpp_qsv instances
            // drops the final frame at EOF

            string padOptions =
                $"pad_w={_paddedSize.Width}:pad_h={_paddedSize.Height}:pad_x=-1:pad_y=-1:pad_color=black";

            string scale;

            if (_currentState.ScaledSize == _scaledSize)
            {
                string format = string.Empty;
                foreach (IPixelFormat pixelFormat in _currentState.PixelFormat)
                {
                    format = $"format={pixelFormat.FFmpegName}:";
                }

                scale = $"vpp_qsv={format}{padOptions}";
            }
            else
            {
                string format = string.Empty;
                foreach (IPixelFormat pixelFormat in _currentState.PixelFormat)
                {
                    format = $":format={pixelFormat.FFmpegName}";
                }

                string squareScale = string.Empty;
                string setsar = string.Empty;
                string sar = _sampleAspectRatio.Replace(':', '/');
                if (_isAnamorphicEdgeCase)
                {
                    squareScale = $"vpp_qsv=w=iw:h={sar}*ih{format},setsar=1,";
                }
                else if (_currentState.IsAnamorphic)
                {
                    squareScale = $"vpp_qsv=w=iw*{sar}:h=ih{format},setsar=1,";
                }
                else
                {
                    setsar = ",setsar=1";
                }

                scale =
                    $"{squareScale}vpp_qsv=w={_scaledSize.Width}:h={_scaledSize.Height}{format}:{padOptions}{setsar}";
            }

            if (_currentState.FrameDataLocation == FrameDataLocation.Hardware)
            {
                return scale;
            }

            string initialPixelFormat = _currentState.PixelFormat.Match(pf => pf.FFmpegName, FFmpegFormat.NV12);
            return $"format={initialPixelFormat},hwupload=extra_hw_frames={_extraHardwareFrames},{scale}";
        }
    }

    public override FrameState NextState(FrameState currentState)
    {
        FrameState result = currentState with
        {
            ScaledSize = _scaledSize,
            PaddedSize = _paddedSize,
            FrameDataLocation = FrameDataLocation.Hardware,
            IsAnamorphic = false // this filter always outputs square pixels
        };

        if (_currentState.PixelFormat.IsNone &&
            _currentState.FrameDataLocation == FrameDataLocation.Software &&
            currentState.PixelFormat.Map(pf => pf is not PixelFormatNv12).IfNone(false))
        {
            // wrap in nv12
            result = result with
            {
                PixelFormat = currentState.PixelFormat
                    .Map(pf => (IPixelFormat)new PixelFormatNv12(pf.Name))
            };
        }
        else
        {
            foreach (IPixelFormat pixelFormat in _currentState.PixelFormat)
            {
                result = result with { PixelFormat = Some(pixelFormat) };
            }
        }

        return result;
    }
}
