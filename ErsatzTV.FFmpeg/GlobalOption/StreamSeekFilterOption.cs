using ErsatzTV.FFmpeg.Environment;

namespace ErsatzTV.FFmpeg.GlobalOption;

public class StreamSeekFilterOption : IPipelineStep
{
    private readonly TimeSpan _start;

    public StreamSeekFilterOption(TimeSpan start) => _start = start;

    public EnvironmentVariable[] EnvironmentVariables => Array.Empty<EnvironmentVariable>();
    public string[] GlobalOptions => Array.Empty<string>();
    public string[] InputOptions(InputFile inputFile) => Array.Empty<string>();

    public string[] FilterOptions => ["-ss", FFmpegFormatter.Milliseconds(_start)];
    public string[] OutputOptions => Array.Empty<string>();
    public FrameState NextState(FrameState currentState) => currentState;
}
