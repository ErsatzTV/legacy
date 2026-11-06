using ErsatzTV.FFmpeg.Capabilities.Qsv;

namespace ErsatzTV.FFmpeg.Capabilities;

public interface IHardwareCapabilitiesFactory
{
    void ClearCache();

    Task<IFFmpegCapabilities> GetFFmpegCapabilities(string ffmpegPath, CancellationToken cancellationToken);

    Task<string> GetFFmpegVersion(string ffmpegPath, CancellationToken cancellationToken);

    Task<IHardwareCapabilities> GetHardwareCapabilities(
        IFFmpegCapabilities ffmpegCapabilities,
        string ffmpegPath,
        HardwareAccelerationMode hardwareAccelerationMode,
        Option<string> vaapiDisplay,
        Option<string> vaapiDriver,
        Option<string> vaapiDevice);

    Task<QsvOutput> GetQsvOutput(string ffmpegPath, Option<string> qsvDevice);

    Task<Option<string>> GetVaapiOutput(string display, Option<string> vaapiDriver, string vaapiDevice);

    Task<List<string>> GetVaapiDisplays();

    List<CpuModel> GetCpuList();

    List<VideoControllerModel> GetVideoControllerList();

    void SetAviSynthInstalled(bool aviSynthInstalled);

    bool IsAviSynthInstalled();
}
