using ErsatzTV.FFmpeg.Format;

namespace ErsatzTV.FFmpeg.Filter.Qsv;

public class PadQsvFilter : BaseFilter
{
    private readonly FrameState _currentState;
    private readonly FrameSize _paddedSize;
    private readonly int _extraHardwareFrames;

    public PadQsvFilter(FrameState currentState, FrameSize paddedSize, int extraHardwareFrames)
    {
        _currentState = currentState;
        _paddedSize = paddedSize;
        _extraHardwareFrames = extraHardwareFrames;
    }

    public override string Filter
    {
        get
        {
            var pad =
                $"vpp_qsv=pad_w={_paddedSize.Width}:pad_h={_paddedSize.Height}:pad_x=-1:pad_y=-1:pad_color=black";

            if (_currentState.FrameDataLocation == FrameDataLocation.Hardware)
            {
                return pad;
            }

            string initialPixelFormat = _currentState.PixelFormat.Match(pf => pf.FFmpegName, FFmpegFormat.NV12);
            return $"format={initialPixelFormat},hwupload=extra_hw_frames={_extraHardwareFrames},{pad}";
        }
    }

    public override FrameState NextState(FrameState currentState) => currentState with
    {
        PaddedSize = _paddedSize,
        FrameDataLocation = FrameDataLocation.Hardware
    };
}
