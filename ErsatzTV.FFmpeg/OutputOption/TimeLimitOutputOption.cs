using ErsatzTV.FFmpeg.Environment;

namespace ErsatzTV.FFmpeg.OutputOption;

public class TimeLimitOutputOption(TimeSpan finish) : IPipelineStep
{
    public EnvironmentVariable[] EnvironmentVariables => [];
    public string[] GlobalOptions => [];
    public string[] InputOptions(InputFile inputFile) => [];
    public string[] FilterOptions => [];
    public string[] OutputOptions => ["-t", FFmpegFormatter.Milliseconds(finish)];
    public FrameState NextState(FrameState currentState) => currentState;
}
