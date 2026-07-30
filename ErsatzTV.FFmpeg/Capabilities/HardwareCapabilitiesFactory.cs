using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.FFmpeg.Capabilities.Nvidia;
using ErsatzTV.FFmpeg.Capabilities.Qsv;
using ErsatzTV.FFmpeg.Capabilities.Vaapi;
using ErsatzTV.FFmpeg.Capabilities.VideoToolbox;
using ErsatzTV.FFmpeg.GlobalOption.HardwareAcceleration;
using ErsatzTV.FFmpeg.Runtime;
using Hardware.Info;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.FFmpeg.Capabilities;

public partial class HardwareCapabilitiesFactory(
    IMemoryCache memoryCache,
    IRuntimeInfo runtimeInfo,
    ILogger<HardwareCapabilitiesFactory> logger)
    : IHardwareCapabilitiesFactory
{
    private const string CudaDeviceKey = "ffmpeg.hardware.cuda.device";
    private const string FFmpegVersionKey = "ffmpeg.version";

    private static readonly CompositeFormat VaapiCacheKeyFormat =
        CompositeFormat.Parse("ffmpeg.hardware.vaapi.{0}.{1}.{2}");

    private static readonly CompositeFormat VaapiGenerationCacheKeyFormat =
        CompositeFormat.Parse("ffmpeg.hardware.vaapi.generation.{0}.{1}.{2}");

    private static readonly CompositeFormat QsvCacheKeyFormat = CompositeFormat.Parse("ffmpeg.hardware.qsv.{0}");
    private static readonly CompositeFormat FFmpegCapabilitiesCacheKeyFormat = CompositeFormat.Parse("ffmpeg.{0}");

    private static readonly CompositeFormat FFmpegFilterOptionsCacheKeyFormat =
        CompositeFormat.Parse("ffmpeg.filter.{0}");

    private static readonly string[] QsvArguments =
    {
        "-f", "lavfi",
        "-i", "nullsrc",
        "-t", "00:00:01",
        "-c:v", "h264_qsv",
        "-f", "null", "-"
    };

    public void ClearCache()
    {
        memoryCache.Remove(FFmpegVersionKey);
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "hwaccels"));
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "decoders"));
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "filters"));
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "encoders"));
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "options"));
        memoryCache.Remove(string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "formats"));
        memoryCache.Remove(
            string.Format(CultureInfo.InvariantCulture, FFmpegFilterOptionsCacheKeyFormat, FFmpegKnownFilter.VppQsv.Name));
    }

    public async Task<IFFmpegCapabilities> GetFFmpegCapabilities(string ffmpegPath, CancellationToken cancellationToken)
    {
        // TODO: validate videotoolbox somehow
        // TODO: validate amf somehow

        string ffmpegVersion = await GetFFmpegVersion(ffmpegPath, cancellationToken);

        IReadOnlySet<string> ffmpegHardwareAccelerations =
            await GetFFmpegCapabilities(ffmpegPath, "hwaccels", ParseFFmpegAccelLine, cancellationToken)
                .Map(set => set.Intersect(FFmpegKnownHardwareAcceleration.AllAccels).ToImmutableHashSet());

        IReadOnlySet<string> ffmpegDecoders =
            await GetFFmpegCapabilities(ffmpegPath, "decoders", ParseFFmpegLine, cancellationToken)
                .Map(set => set.Intersect(FFmpegKnownDecoder.AllDecoders).ToImmutableHashSet());

        IEnumerable<string> allFilterNames =
            FFmpegKnownFilter.AllFilters.Union(FFmpegKnownFilter.RequiredFilters.Select(f => f.Name));
        IReadOnlySet<string> ffmpegFilters =
            await GetFFmpegCapabilities(ffmpegPath, "filters", ParseFFmpegLine, cancellationToken)
                .Map(set => set.Intersect(allFilterNames).ToImmutableHashSet());

        IReadOnlySet<string> ffmpegEncoders =
            await GetFFmpegCapabilities(ffmpegPath, "encoders", ParseFFmpegLine, cancellationToken)
                .Map(set => set.Intersect(FFmpegKnownEncoder.AllEncoders).ToImmutableHashSet());

        IReadOnlySet<string> ffmpegOptions = await GetFFmpegOptions(ffmpegPath, cancellationToken)
            .Map(set => set.Intersect(FFmpegKnownOption.AllOptions).ToImmutableHashSet());

        IReadOnlySet<string> ffmpegDemuxFormats = await GetFFmpegFormats(ffmpegPath, "D", cancellationToken)
            .Map(set => set.Intersect(FFmpegKnownFormat.AllFormats).ToImmutableHashSet());

        IReadOnlySet<string> vppQsvOptions =
            await GetFFmpegFilterOptions(ffmpegPath, FFmpegKnownFilter.VppQsv.Name, cancellationToken);
        var ffmpegFilterOptions = new Dictionary<string, IReadOnlySet<string>>
        {
            [FFmpegKnownFilter.VppQsv.Name] = vppQsvOptions
        };

        return new FFmpegCapabilities(
            ffmpegVersion,
            ffmpegHardwareAccelerations,
            ffmpegDecoders,
            ffmpegFilters,
            ffmpegEncoders,
            ffmpegOptions,
            ffmpegDemuxFormats,
            ffmpegFilterOptions);
    }

    public async Task<IHardwareCapabilities> GetHardwareCapabilities(
        IFFmpegCapabilities ffmpegCapabilities,
        string ffmpegPath,
        HardwareAccelerationMode hardwareAccelerationMode,
        Option<string> vaapiDisplay,
        Option<string> vaapiDriver,
        Option<string> vaapiDevice)
    {
        if (hardwareAccelerationMode is HardwareAccelerationMode.None)
        {
            return new NoHardwareCapabilities();
        }

        if (!ffmpegCapabilities.HasHardwareAcceleration(hardwareAccelerationMode))
        {
            logger.LogWarning(
                "FFmpeg does not support {HardwareAcceleration} acceleration; will use software mode",
                hardwareAccelerationMode);

            return new NoHardwareCapabilities();
        }

        return hardwareAccelerationMode switch
        {
            HardwareAccelerationMode.Nvenc => GetNvidiaCapabilities(ffmpegCapabilities),
            HardwareAccelerationMode.Qsv => await GetQsvCapabilities(ffmpegPath, vaapiDevice),
            HardwareAccelerationMode.Vaapi => await GetVaapiCapabilities(vaapiDisplay, vaapiDriver, vaapiDevice),
            HardwareAccelerationMode.VideoToolbox => new VideoToolboxHardwareCapabilities(ffmpegCapabilities, logger),
            HardwareAccelerationMode.Amf => new AmfHardwareCapabilities(),
            HardwareAccelerationMode.V4l2m2m => new V4l2m2mHardwareCapabilities(ffmpegCapabilities),
            HardwareAccelerationMode.Rkmpp => new RkmppHardwareCapabilities(),
            _ => new DefaultHardwareCapabilities()
        };
    }

    public async Task<string> GetNvidiaOutput(string ffmpegPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return string.Empty;
        }

        try
        {
            Option<List<CudaDevice>> maybeDevices = CudaHelper.GetDevices();
            foreach (List<CudaDevice> devices in maybeDevices.Where(list => list.Count > 0))
            {
                var sb = new StringBuilder();
                foreach (CudaDevice device in devices)
                {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"GPU #{device.Handle} < {device.Model} > has Compute SM {device.Version.Major}.{device.Version.Minor}");

                    sb.AppendLine(CudaHelper.GetDeviceDetails(device));
                }

                return sb.ToString();
            }
        }
        catch (FileNotFoundException)
        {
            // do nothing
        }
        catch (TypeInitializationException)
        {
            // do nothing
        }

        // if we don't have a list of cuda devices, fall back to ffmpeg check

        string[] arguments =
        [
            "-f", "lavfi",
            "-i", "nullsrc",
            "-c:v", "h264_nvenc",
            "-gpu", "list",
            "-f", "null", "-"
        ];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        return output;
    }

    public async Task<QsvOutput> GetQsvOutput(string ffmpegPath, Option<string> qsvDevice)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new QsvOutput(0, string.Empty);
        }

        var option = new QsvHardwareAccelerationOption(qsvDevice, FFmpegCapability.Software);
        var arguments = option.GlobalOptions.ToList();

        arguments.AddRange(QsvArguments);

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        return new QsvOutput(result.ExitCode, output);
    }

    public async Task<Option<string>> GetVaapiOutput(string display, Option<string> vaapiDriver, string vaapiDevice)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Option<string>.None;
        }

        BufferedCommandResult whichResult = await Cli.Wrap("which")
            .WithArguments("vainfo")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        if (whichResult.ExitCode != 0)
        {
            return Option<string>.None;
        }

        var envVars = new Dictionary<string, string?>();
        foreach (string libvaDriverName in vaapiDriver)
        {
            envVars.Add("LIBVA_DRIVER_NAME", libvaDriverName);
        }

        var lines = new List<string>();

        string arguments = display == "drm"
            ? $"--display drm --device {vaapiDevice} -a"
            : $"--display {display} -a";

        await Cli.Wrap("vainfo")
            .WithArguments(arguments)
            .WithEnvironmentVariables(envVars)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(lines.Add))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(lines.Add))
            .ExecuteAsync();

        return string.Join(System.Environment.NewLine, lines);
    }

    public async Task<List<string>> GetVaapiDisplays()
    {
        BufferedCommandResult whichResult = await Cli.Wrap("which")
            .WithArguments("vainfo")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        if (whichResult.ExitCode != 0)
        {
            return ["drm"];
        }

        BufferedCommandResult result = await Cli.Wrap("vainfo")
            .WithArguments("--display help")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8);

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? ["drm"]
            : result.StandardOutput.Trim().Split("\n").Skip(1).Map(s => s.Trim()).ToList();
    }

    public List<CpuModel> GetCpuList()
    {
        try
        {
            var hardwareInfo = new HardwareInfo();
            hardwareInfo.RefreshCPUList();
            return hardwareInfo.CpuList.Map(c => new CpuModel(c.Manufacturer, c.Name)).ToList();
        }
        catch (Exception)
        {
            // do nothing
        }

        return [];
    }

    public List<VideoControllerModel> GetVideoControllerList()
    {
        try
        {
            var hardwareInfo = new HardwareInfo();
            hardwareInfo.RefreshVideoControllerList();
            return hardwareInfo.VideoControllerList
                .Map(v => new VideoControllerModel(v.Manufacturer, v.Name))
                .ToList();
        }
        catch (Exception)
        {
            // do nothing
        }

        return [];
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public List<string> GetVideoToolboxDecoders()
    {
        var result = new List<string>();

        foreach (string fourCC in FourCC.AllVideoToolbox)
        {
            if (VideoToolboxUtil.IsHardwareDecoderSupported(fourCC, logger))
            {
                result.Add(fourCC);
            }
        }

        return result;
    }

    public List<string> GetVideoToolboxEncoders() => VideoToolboxUtil.GetAvailableEncoders(logger);

    public void SetAviSynthInstalled(bool aviSynthInstalled)
    {
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            FFmpegCapabilitiesCacheKeyFormat,
            "avisynth_installed");

        memoryCache.Set(cacheKey, aviSynthInstalled);
    }

    public bool IsAviSynthInstalled()
    {
        var cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            FFmpegCapabilitiesCacheKeyFormat,
            "avisynth_installed");

        return memoryCache.TryGetValue(cacheKey, out bool installed) && installed;
    }

    public async Task<string> GetFFmpegVersion(string ffmpegPath, CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(FFmpegVersionKey, out string? ffmpegVersion) &&
            ffmpegVersion is not null)
        {
            return ffmpegVersion;
        }

        string[] arguments = ["-version"];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        string versionResult = await output.Split("\n").Map(s => s.Trim())
            .Bind(l => ParseFFmpegVersionLine(l))
            .HeadOrNone()
            .IfNoneAsync(string.Empty);

        memoryCache.Set(FFmpegVersionKey, versionResult);

        return versionResult;
    }

    private async Task<IReadOnlySet<string>> GetFFmpegCapabilities(
        string ffmpegPath,
        string capabilities,
        Func<string, Option<string>> parseLine,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, capabilities);
        if (memoryCache.TryGetValue(cacheKey, out IReadOnlySet<string>? cachedCapabilities) &&
            cachedCapabilities is not null)
        {
            return cachedCapabilities;
        }

        string[] arguments = ["-hide_banner", $"-{capabilities}"];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        var capabilitiesResult = output.Split("\n").Map(s => s.Trim())
            .Bind(l => parseLine(l))
            .ToImmutableHashSet();

        memoryCache.Set(cacheKey, capabilitiesResult);

        return capabilitiesResult;
    }

    private async Task<IReadOnlySet<string>> GetFFmpegOptions(string ffmpegPath, CancellationToken cancellationToken)
    {
        var cacheKey = string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "options");
        if (memoryCache.TryGetValue(cacheKey, out IReadOnlySet<string>? cachedCapabilities) &&
            cachedCapabilities is not null)
        {
            return cachedCapabilities;
        }

        string[] arguments = ["-hide_banner", "-h", "long"];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        var capabilitiesResult = output.Split("\n").Map(s => s.Trim())
            .Bind(l => ParseFFmpegOptionLine(l))
            .ToImmutableHashSet();

        memoryCache.Set(cacheKey, capabilitiesResult);

        return capabilitiesResult;
    }

    private async Task<IReadOnlySet<string>> GetFFmpegFilterOptions(
        string ffmpegPath,
        string filterName,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Format(CultureInfo.InvariantCulture, FFmpegFilterOptionsCacheKeyFormat, filterName);
        if (memoryCache.TryGetValue(cacheKey, out IReadOnlySet<string>? cachedCapabilities) &&
            cachedCapabilities is not null)
        {
            return cachedCapabilities;
        }

        string[] arguments = ["-hide_banner", "-h", $"filter={filterName}"];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        IReadOnlySet<string> capabilitiesResult = FFmpegFilterOptionParser.Parse(output);

        memoryCache.Set(cacheKey, capabilitiesResult);

        return capabilitiesResult;
    }

    private async Task<IReadOnlySet<string>> GetFFmpegFormats(
        string ffmpegPath,
        string muxDemux,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Format(CultureInfo.InvariantCulture, FFmpegCapabilitiesCacheKeyFormat, "formats");
        if (memoryCache.TryGetValue(cacheKey, out IReadOnlySet<string>? cachedCapabilities) &&
            cachedCapabilities is not null)
        {
            return cachedCapabilities;
        }

        string[] arguments = ["-hide_banner", "-formats"];

        BufferedCommandResult result = await Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        var capabilitiesResult = output.Split("\n").Map(s => s.Trim())
            .Bind(l => ParseFFmpegFormatLine(l))
            .Where(tuple => tuple.Item1.Contains(muxDemux))
            .Map(tuple => tuple.Item2)
            .ToImmutableHashSet();

        memoryCache.Set(cacheKey, capabilitiesResult);

        return capabilitiesResult;
    }

    private static Option<string> ParseFFmpegVersionLine(string input)
    {
        Match match = VersionRegex().Match(input);
        return match.Success ? match.Groups[1].Value : Option<string>.None;
    }

    private static Option<string> ParseFFmpegAccelLine(string input)
    {
        Match match = AccelRegex().Match(input);
        return match.Success ? match.Groups[1].Value : Option<string>.None;
    }

    private static Option<string> ParseFFmpegLine(string input)
    {
        Match match = FFmpegRegex().Match(input);
        return match.Success ? match.Groups[1].Value : Option<string>.None;
    }

    private static Option<string> ParseFFmpegOptionLine(string input)
    {
        Match match = OptionRegex().Match(input);
        return match.Success ? match.Groups[1].Value : Option<string>.None;
    }

    private static Option<Tuple<string, string>> ParseFFmpegFormatLine(string input)
    {
        Match match = FormatRegex().Match(input);
        return match.Success ? Tuple(match.Groups[1].Value, match.Groups[2].Value) : Option<Tuple<string, string>>.None;
    }

    private async Task<IHardwareCapabilities> GetVaapiCapabilities(
        Option<string> vaapiDisplay,
        Option<string> vaapiDriver,
        Option<string> vaapiDevice)
    {
        try
        {
            if (vaapiDevice.IsNone)
            {
                // this shouldn't really happen

                logger.LogError(
                    "Cannot detect VAAPI capabilities without device {Device}",
                    vaapiDevice);

                return new NoHardwareCapabilities();
            }

            string display = await vaapiDisplay.IfNoneAsync("drm");
            string driver = await vaapiDriver.IfNoneAsync(string.Empty);
            string device = await vaapiDevice.IfNoneAsync(string.Empty);
            string generation = string.Empty;
            var cacheKey = string.Format(CultureInfo.InvariantCulture, VaapiCacheKeyFormat, display, driver, device);
            var generationCacheKey = string.Format(
                CultureInfo.InvariantCulture,
                VaapiGenerationCacheKeyFormat,
                display,
                driver,
                device);

            if (memoryCache.TryGetValue(cacheKey, out List<VaapiProfileEntrypoint>? profileEntrypoints) &&
                profileEntrypoints is not null)
            {
                if (memoryCache.TryGetValue(generationCacheKey, out string? cachedGeneration) &&
                    cachedGeneration is not null)
                {
                    generation = cachedGeneration;
                }

                return new VaapiHardwareCapabilities(profileEntrypoints, generation, logger);
            }

            Option<string> output = await GetVaapiOutput(display, vaapiDriver, device);
            if (output.IsNone)
            {
                logger.LogWarning("Unable to determine VAAPI capabilities; please install vainfo");
                return new DefaultHardwareCapabilities();
            }

            foreach (string o in output)
            {
                profileEntrypoints = VaapiCapabilityParser.ParseFull(o);
                generation = VaapiCapabilityParser.ParseGeneration(o);
            }

            if (profileEntrypoints is not null && profileEntrypoints.Count != 0)
            {
                if (display == "drm")
                {
                    logger.LogDebug(
                        "Detected {Count} VAAPI profile entrypoints using {Driver} {Device}",
                        profileEntrypoints.Count,
                        driver,
                        device);
                }
                else
                {
                    logger.LogDebug(
                        "Detected {Count} VAAPI profile entrypoints using {Display} {Driver}",
                        profileEntrypoints.Count,
                        display,
                        driver);
                }

                memoryCache.Set(cacheKey, profileEntrypoints);
                memoryCache.Set(generationCacheKey, generation);
                return new VaapiHardwareCapabilities(profileEntrypoints, generation, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Error detecting VAAPI capabilities; some hardware accelerated features will be unavailable");
            return new NoHardwareCapabilities();
        }

        logger.LogWarning(
            "Error detecting VAAPI capabilities; some hardware accelerated features will be unavailable");

        return new NoHardwareCapabilities();
    }

    private async Task<IHardwareCapabilities> GetQsvCapabilities(string ffmpegPath, Option<string> qsvDevice)
    {
        try
        {
            if (runtimeInfo.IsOSPlatform(OSPlatform.Linux) && qsvDevice.IsNone)
            {
                // this shouldn't really happen
                logger.LogError("Cannot detect QSV capabilities without device {Device}", qsvDevice);
                return new NoHardwareCapabilities();
            }

            string device = await qsvDevice.IfNoneAsync(string.Empty);
            var cacheKey = string.Format(CultureInfo.InvariantCulture, QsvCacheKeyFormat, device);

            if (memoryCache.TryGetValue(cacheKey, out List<VaapiProfileEntrypoint>? profileEntrypoints) &&
                profileEntrypoints is not null)
            {
                return new VaapiHardwareCapabilities(profileEntrypoints, string.Empty, logger);
            }

            QsvOutput output = await GetQsvOutput(ffmpegPath, qsvDevice);
            if (output.ExitCode != 0)
            {
                logger.LogWarning("QSV test failed; some hardware accelerated features will be unavailable");
                return new NoHardwareCapabilities();
            }

            if (runtimeInfo.IsOSPlatform(OSPlatform.Linux))
            {
                if (!memoryCache.TryGetValue("ffmpeg.vaapi_displays", out List<string>? vaapiDisplays))
                {
                    vaapiDisplays = ["drm"];
                }

                vaapiDisplays ??= [];
                vaapiDisplays = vaapiDisplays.OrderBy(s => s).ToList();

                foreach (string vaapiDisplay in vaapiDisplays)
                {
                    Option<string> vaapiOutput = await GetVaapiOutput(vaapiDisplay, Option<string>.None, device);
                    if (vaapiOutput.IsNone)
                    {
                        logger.LogWarning("Unable to determine QSV capabilities; please install vainfo");
                        return new DefaultHardwareCapabilities();
                    }

                    foreach (string o in vaapiOutput)
                    {
                        profileEntrypoints = VaapiCapabilityParser.ParseFull(o);
                    }

                    if (profileEntrypoints is not null && profileEntrypoints.Count != 0)
                    {
                        logger.LogDebug(
                            "Detected {Count} VAAPI profile entrypoints using QSV device {Device}",
                            profileEntrypoints.Count,
                            device);

                        memoryCache.Set(cacheKey, profileEntrypoints);
                        return new VaapiHardwareCapabilities(profileEntrypoints, string.Empty, logger);
                    }
                }
            }

            // not sure how to check capabilities on windows
            return new DefaultHardwareCapabilities();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Error detecting QSV capabilities; some hardware accelerated features will be unavailable");
            return new NoHardwareCapabilities();
        }
    }

    private IHardwareCapabilities GetNvidiaCapabilities(IFFmpegCapabilities ffmpegCapabilities)
    {
        if (memoryCache.TryGetValue(CudaDeviceKey, out CudaDevice? cudaDevice) && cudaDevice is not null)
        {
            return new NvidiaHardwareCapabilities(cudaDevice, ffmpegCapabilities, logger);
        }

        try
        {
            Option<List<CudaDevice>> maybeDevices = CudaHelper.GetDevices();
            foreach (CudaDevice firstDevice in maybeDevices.Map(list => list.HeadOrNone()))
            {
                logger.LogDebug(
                    "Detected NVIDIA GPU model {Model} architecture SM {Major}.{Minor}",
                    firstDevice.Model,
                    firstDevice.Version.Major,
                    firstDevice.Version.Minor);

                memoryCache.Set(CudaDeviceKey, firstDevice);
                return new NvidiaHardwareCapabilities(firstDevice, ffmpegCapabilities, logger);
            }
        }
        catch (FileNotFoundException)
        {
            // do nothing
        }
        catch (TypeInitializationException)
        {
            // do nothing
        }

        logger.LogWarning(
            "Error detecting NVIDIA GPU capabilities; some hardware accelerated features will be unavailable");

        return new NoHardwareCapabilities();
    }

    [GeneratedRegex(@"version\s+([^\s]+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^([\w]+)$")]
    private static partial Regex AccelRegex();

    [GeneratedRegex(@"^\s*?[A-Z\.]+\s+(\w+).*")]
    private static partial Regex FFmpegRegex();

    [GeneratedRegex(@"^-([a-z_]+)\s+.*")]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"([DE]+)\s+(\w+)")]
    private static partial Regex FormatRegex();
}
