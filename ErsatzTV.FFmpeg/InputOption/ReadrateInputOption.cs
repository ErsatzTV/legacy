using System.Globalization;
using ErsatzTV.FFmpeg.Environment;

namespace ErsatzTV.FFmpeg.InputOption;

public class ReadrateInputOption(double readRate, double initialBurstSeconds = 0) : IInputOption
{
    public EnvironmentVariable[] EnvironmentVariables => [];

    public string[] GlobalOptions => [];

    public string[] InputOptions(InputFile inputFile)
    {
        var result = new List<string>
        {
            "-readrate",
            readRate.ToString("0.0####", CultureInfo.InvariantCulture)
        };

        if (initialBurstSeconds > 0)
        {
            result.Add("-readrate_initial_burst");
            result.Add(initialBurstSeconds.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return result.ToArray();
    }

    public string[] FilterOptions => [];
    public string[] OutputOptions => [];
    public FrameState NextState(FrameState currentState) => currentState with { Realtime = true };

    public bool AppliesTo(AudioInputFile audioInputFile) => audioInputFile is not NullAudioInputFile;

    // don't use realtime input for a still image
    public bool AppliesTo(VideoInputFile videoInputFile) => videoInputFile.VideoStreams.All(s => !s.StillImage);

    public bool AppliesTo(ConcatInputFile concatInputFile) => true;

    public bool AppliesTo(GraphicsEngineInput graphicsEngineInput) => false;
}
