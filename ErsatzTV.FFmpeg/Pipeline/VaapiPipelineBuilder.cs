using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Decoder;
using ErsatzTV.FFmpeg.Encoder;
using ErsatzTV.FFmpeg.Encoder.Vaapi;
using ErsatzTV.FFmpeg.Environment;
using ErsatzTV.FFmpeg.Filter;
using ErsatzTV.FFmpeg.Filter.Vaapi;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.GlobalOption.HardwareAcceleration;
using ErsatzTV.FFmpeg.InputOption;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.FFmpeg.OutputOption;
using ErsatzTV.FFmpeg.State;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.FFmpeg.Pipeline;

public class VaapiPipelineBuilder : SoftwarePipelineBuilder
{
    private readonly IFFmpegCapabilities _ffmpegCapabilities;
    private readonly IHardwareCapabilities _hardwareCapabilities;
    private readonly ILogger _logger;

    public VaapiPipelineBuilder(
        IFFmpegCapabilities ffmpegCapabilities,
        IHardwareCapabilities hardwareCapabilities,
        HardwareAccelerationMode hardwareAccelerationMode,
        Option<VideoInputFile> videoInputFile,
        Option<AudioInputFile> audioInputFile,
        Option<WatermarkInputFile> watermarkInputFile,
        Option<SubtitleInputFile> subtitleInputFile,
        Option<ConcatInputFile> concatInputFile,
        Option<GraphicsEngineInput> graphicsEngineInput,
        string reportsFolder,
        string fontsFolder,
        ILogger logger) : base(
        ffmpegCapabilities,
        hardwareAccelerationMode,
        videoInputFile,
        audioInputFile,
        watermarkInputFile,
        subtitleInputFile,
        concatInputFile,
        graphicsEngineInput,
        reportsFolder,
        fontsFolder,
        logger)
    {
        _ffmpegCapabilities = ffmpegCapabilities;
        _hardwareCapabilities = hardwareCapabilities;
        _logger = logger;
    }

    // check for intel vaapi (NOT radeon)
    protected override bool IsIntelVaapiOrQsv(FFmpegState ffmpegState) =>
        (ffmpegState.DecoderHardwareAccelerationMode is HardwareAccelerationMode.Vaapi ||
         ffmpegState.EncoderHardwareAccelerationMode is HardwareAccelerationMode.Vaapi) &&
        !ffmpegState.VaapiDriver.IfNone(string.Empty).StartsWith("radeon", StringComparison.OrdinalIgnoreCase);

    protected override FFmpegState SetAccelState(
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        PipelineContext context,
        ICollection<IPipelineStep> pipelineSteps)
    {
        FFmpegCapability decodeCapability = _hardwareCapabilities.CanDecode(
            videoStream.Codec,
            videoStream.Profile,
            videoStream.PixelFormat,
            videoStream.ColorParams);
        FFmpegCapability encodeCapability = _hardwareCapabilities.CanEncode(
            desiredState.VideoFormat,
            desiredState.VideoProfile,
            desiredState.PixelFormat);

        // use software encoding (rawvideo) when piping to parent hls segmenter
        if (ffmpegState.OutputFormat is OutputFormatKind.Nut)
        {
            encodeCapability = FFmpegCapability.Software;
        }

        if (desiredState.VideoFormat is VideoFormat.Av1 && ffmpegState.OutputFormat is not OutputFormatKind.HlsMp4)
        {
            throw new NotSupportedException("AV1 output is only supported with HLS Segmenter (fmp4)");
        }

        // use software decoding with an extensive pipeline
        if (context is { HasSubtitleOverlay: true, HasWatermark: true })
        {
            decodeCapability = FFmpegCapability.Software;
        }

        // use software decode with irregular dimensions and AMD Polaris
        if (videoStream.FrameSize.Width % 32 != 0 &&
            _hardwareCapabilities is VaapiHardwareCapabilities vaapiCapabilities)
        {
            if (vaapiCapabilities.Generation.Contains("polaris", StringComparison.OrdinalIgnoreCase))
            {
                decodeCapability = FFmpegCapability.Software;
            }
        }

        foreach (string vaapiDevice in ffmpegState.VaapiDevice)
        {
            pipelineSteps.Add(new VaapiHardwareAccelerationOption(vaapiDevice, decodeCapability));

            foreach (string driverName in ffmpegState.VaapiDriver)
            {
                pipelineSteps.Add(new LibvaDriverNameVariable(driverName));
            }
        }

        // disable auto scaling when using hw encoding
        if (encodeCapability is FFmpegCapability.Hardware)
        {
            pipelineSteps.Add(new NoAutoScaleOutputOption());
        }

        // disable hw accel if decoder/encoder isn't supported
        return ffmpegState with
        {
            DecoderHardwareAccelerationMode = decodeCapability == FFmpegCapability.Hardware
                ? HardwareAccelerationMode.Vaapi
                : HardwareAccelerationMode.None,
            EncoderHardwareAccelerationMode = encodeCapability == FFmpegCapability.Hardware
                ? HardwareAccelerationMode.Vaapi
                : HardwareAccelerationMode.None
        };
    }

    protected override Option<IDecoder> SetDecoder(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        PipelineContext context)
    {
        Option<IDecoder> maybeDecoder = (ffmpegState.DecoderHardwareAccelerationMode, videoStream.Codec) switch
        {
            (HardwareAccelerationMode.Vaapi, _) => new DecoderVaapi(),
            _ => GetSoftwareDecoder(videoStream)
        };

        foreach (IDecoder decoder in maybeDecoder)
        {
            videoInputFile.AddOption(decoder);
            return Some(decoder);
        }

        return None;
    }

    protected override FilterChain SetVideoFilters(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        Option<WatermarkInputFile> watermarkInputFile,
        Option<SubtitleInputFile> subtitleInputFile,
        Option<GraphicsEngineInput> graphicsEngineInput,
        PipelineContext context,
        Option<IDecoder> maybeDecoder,
        FFmpegState ffmpegState,
        FrameState desiredState,
        string fontsFolder,
        ICollection<IPipelineStep> pipelineSteps)
    {
        var watermarkOverlayFilterSteps = new List<IPipelineFilterStep>();
        var subtitleOverlayFilterSteps = new List<IPipelineFilterStep>();
        var graphicsEngineOverlayFilterSteps = new List<IPipelineFilterStep>();

        FrameState currentState = desiredState with
        {
            ScaledSize = videoStream.FrameSize,
            PaddedSize = videoStream.FrameSize,
            PixelFormat = videoStream.PixelFormat,
            IsAnamorphic = videoStream.IsAnamorphic
        };

        foreach (IDecoder decoder in maybeDecoder)
        {
            currentState = decoder.NextState(currentState);
        }

        // easier to use nv12 for overlay
        if (context.HasSubtitleOverlay || context.HasWatermark || context.HasGraphicsEngine)
        {
            IPixelFormat pixelFormat = desiredState.PixelFormat.IfNone(
                context.Is10BitOutput ? new PixelFormatYuv420P10Le() : new PixelFormatYuv420P());
            desiredState = desiredState with { PixelFormat = new PixelFormatNv12(pixelFormat.Name) };
        }

        // _logger.LogDebug("After decode: {PixelFormat}", currentState.PixelFormat);

        currentState = SetDeinterlace(videoInputFile, context, ffmpegState, currentState);
        // _logger.LogDebug("After deinterlace: {PixelFormat}", currentState.PixelFormat);

        currentState = SetScale(videoInputFile, videoStream, context, ffmpegState, desiredState, currentState);
        // _logger.LogDebug("After scale: {PixelFormat}", currentState.PixelFormat);

        bool isHdrTonemap = videoStream.ColorParams.IsHdr;
        currentState = SetTonemap(videoInputFile, videoStream, ffmpegState, desiredState, currentState);

        currentState = SetPad(videoInputFile, desiredState, currentState, isHdrTonemap);
        // _logger.LogDebug("After pad: {PixelFormat}", currentState.PixelFormat);

        currentState = SetCrop(videoInputFile, desiredState, currentState);

        SetStillImageLoop(videoInputFile, videoStream, ffmpegState, desiredState, pipelineSteps);

        // need to upload for hardware overlay
        bool forceSoftwareOverlay = context is { HasSubtitleOverlay: true, HasWatermark: true }
                                    || ffmpegState.VaapiDriver == "radeonsi";

        if (currentState.FrameDataLocation == FrameDataLocation.Software && context.HasSubtitleOverlay &&
            !forceSoftwareOverlay)
        {
            var hardwareUpload = new HardwareUploadVaapiFilter(true);
            currentState = hardwareUpload.NextState(currentState);
            videoInputFile.FilterSteps.Add(hardwareUpload);
        }
        else if (currentState.FrameDataLocation == FrameDataLocation.Hardware &&
                 (!context.HasSubtitleOverlay || forceSoftwareOverlay) &&
                 (context.HasWatermark || context.HasGraphicsEngine))
        {
            // download for watermark (or forced software subtitle)
            var hardwareDownload = new HardwareDownloadFilter(currentState);
            currentState = hardwareDownload.NextState(currentState);
            videoInputFile.FilterSteps.Add(hardwareDownload);
        }

        currentState = SetSubtitle(
            videoInputFile,
            subtitleInputFile,
            context,
            forceSoftwareOverlay,
            currentState,
            desiredState,
            fontsFolder,
            subtitleOverlayFilterSteps);

        currentState = SetWatermark(
            videoStream,
            watermarkInputFile,
            context,
            desiredState,
            currentState,
            watermarkOverlayFilterSteps);

        SetGraphicsEngine(graphicsEngineInput, currentState, desiredState, graphicsEngineOverlayFilterSteps);

        // after everything else is done, apply the encoder
        if (pipelineSteps.OfType<IEncoder>().All(e => e.Kind != StreamKind.Video))
        {
            bool packedHeaderMisc = false;
            if (_hardwareCapabilities is VaapiHardwareCapabilities vaapiHardwareCapabilities)
            {
                packedHeaderMisc = vaapiHardwareCapabilities.GetPackedHeaderMisc(
                    desiredState.VideoFormat,
                    desiredState.PixelFormat);
            }

            RateControlMode rateControlMode =
                _hardwareCapabilities.GetRateControlMode(desiredState.VideoFormat, desiredState.PixelFormat)
                    .IfNone(RateControlMode.VBR);

            Option<IEncoder> maybeEncoder =
                (ffmpegState.EncoderHardwareAccelerationMode, desiredState.VideoFormat) switch
                {
                    (HardwareAccelerationMode.Vaapi, VideoFormat.Av1) =>
                        new EncoderAv1Vaapi(rateControlMode),
                    (HardwareAccelerationMode.Vaapi, VideoFormat.Hevc) =>
                        new EncoderHevcVaapi(rateControlMode, packedHeaderMisc),
                    (HardwareAccelerationMode.Vaapi, VideoFormat.H264) =>
                        new EncoderH264Vaapi(desiredState.VideoProfile, rateControlMode, packedHeaderMisc),
                    (HardwareAccelerationMode.Vaapi, VideoFormat.Mpeg2Video) =>
                        new EncoderMpeg2Vaapi(rateControlMode),

                    (_, _) => GetSoftwareEncoder(ffmpegState, currentState, desiredState)
                };

            foreach (IEncoder encoder in maybeEncoder)
            {
                pipelineSteps.Add(encoder);
                videoInputFile.FilterSteps.Add(encoder);
            }
        }

        List<IPipelineFilterStep> pixelFormatFilterSteps = SetPixelFormat(
            videoStream,
            desiredState,
            ffmpegState,
            currentState,
            pipelineSteps);

        return new FilterChain(
            videoInputFile.FilterSteps,
            watermarkInputFile.Map(wm => wm.FilterSteps).IfNone([]),
            subtitleInputFile.Map(st => st.FilterSteps).IfNone([]),
            graphicsEngineInput.Map(ge => ge.FilterSteps).IfNone([]),
            watermarkOverlayFilterSteps,
            subtitleOverlayFilterSteps,
            graphicsEngineOverlayFilterSteps,
            pixelFormatFilterSteps);
    }

    private List<IPipelineFilterStep> SetPixelFormat(
        VideoStream videoStream,
        FrameState desiredState,
        FFmpegState ffmpegState,
        FrameState currentState,
        ICollection<IPipelineStep> pipelineSteps)
    {
        var result = new List<IPipelineFilterStep>();

        foreach (IPixelFormat pixelFormat in desiredState.PixelFormat)
        {
            IPixelFormat format = pixelFormat;

            if (pixelFormat is PixelFormatNv12 nv12)
            {
                foreach (IPixelFormat pf in AvailablePixelFormats.ForPixelFormat(nv12.Name, null))
                {
                    format = pf;
                }
            }

            if (desiredState.ColorsAreBt709 && !videoStream.ColorParams.IsBt709)
            {
                // _logger.LogDebug("Adding colorspace filter");
                var colorspace = new ColorspaceFilter(
                    currentState,
                    videoStream,
                    format);
                currentState = colorspace.NextState(currentState);
                result.Add(colorspace);
            }

            if (ffmpegState.EncoderHardwareAccelerationMode == HardwareAccelerationMode.None)
            {
                _logger.LogDebug("Using software encoder");

                if (currentState.FrameDataLocation == FrameDataLocation.Hardware)
                {
                    _logger.LogDebug(
                        "FrameDataLocation == FrameDataLocation.Hardware, {CurrentPixelFormat} bit => {DesiredPixelFormat}",
                        currentState.PixelFormat,
                        desiredState.PixelFormat);

                    // don't try to download from 8-bit to 10-bit, or 10-bit to 8-bit
                    HardwareDownloadFilter hardwareDownload =
                        currentState.BitDepth == 8 && desiredState.PixelFormat.Map(pf => pf.BitDepth).IfNone(8) == 10 ||
                        currentState.BitDepth == 10 && desiredState.PixelFormat.Map(pf => pf.BitDepth).IfNone(10) == 8
                            ? new HardwareDownloadFilter(currentState)
                            : new HardwareDownloadFilter(currentState with { PixelFormat = Some(format) });

                    currentState = hardwareDownload.NextState(currentState);
                    result.Add(hardwareDownload);
                }
            }

            if (currentState.PixelFormat.Map(f => f.FFmpegName) != format.FFmpegName)
            {
                _logger.LogDebug(
                    "Format {A} doesn't equal {B}",
                    currentState.PixelFormat.Map(f => f.FFmpegName),
                    format.FFmpegName);

                // NV12 is 8-bit, and Intel VAAPI seems to REQUIRE NV12
                // NUT is fine with YUV420P
                if (format is PixelFormatYuv420P && ffmpegState.OutputFormat is not OutputFormatKind.Nut)
                {
                    format = new PixelFormatNv12(format.Name);
                }

                if (currentState.FrameDataLocation == FrameDataLocation.Hardware)
                {
                        result.Add(new VaapiFormatFilter(format));
                }
                else
                {
                    if (ffmpegState.EncoderHardwareAccelerationMode is HardwareAccelerationMode.Vaapi)
                    {
                        result.Add(new PixelFormatFilter(format));
                    }
                    else
                    {
                        pipelineSteps.Add(new PixelFormatOutputOption(format));
                    }
                }
            }

            if (ffmpegState.EncoderHardwareAccelerationMode == HardwareAccelerationMode.Vaapi &&
                currentState.FrameDataLocation == FrameDataLocation.Software)
            {
                bool setFormat = result.All(f => f is not VaapiFormatFilter && f is not PixelFormatFilter);
                result.Add(new HardwareUploadVaapiFilter(setFormat));
            }
        }

        return result;
    }

    private FrameState SetWatermark(
        VideoStream videoStream,
        Option<WatermarkInputFile> watermarkInputFile,
        PipelineContext context,
        FrameState desiredState,
        FrameState currentState,
        List<IPipelineFilterStep> watermarkOverlayFilterSteps)
    {
        if (context.HasWatermark)
        {
            WatermarkInputFile watermark = watermarkInputFile.Head();

            foreach (VideoStream watermarkStream in watermark.VideoStreams)
            {
                if (!watermarkStream.StillImage)
                {
                    watermark.AddOption(new DoNotIgnoreLoopInputOption());
                }
                else if (watermark.DesiredState.MaybeFadePoints.Map(fp => fp.Count > 0).IfNone(false))
                {
                    // looping is required to fade a static image in and out
                    watermark.AddOption(new InfiniteLoopInputOption(HardwareAccelerationMode.None));
                }
            }

            if (watermark.DesiredState.Size == WatermarkSize.Scaled)
            {
                watermark.FilterSteps.Add(
                    new WatermarkScaleFilter(watermark.DesiredState, currentState.PaddedSize));
            }

            if (watermark.DesiredState.Opacity != 100)
            {
                watermark.FilterSteps.Add(new WatermarkOpacityFilter(watermark.DesiredState));
            }

            watermark.FilterSteps.Add(new PixelFormatFilter(new PixelFormatYuva420P()));

            foreach (List<WatermarkFadePoint> fadePoints in watermark.DesiredState.MaybeFadePoints)
            {
                watermark.FilterSteps.AddRange(fadePoints.Map(fp => new WatermarkFadeFilter(fp)));
            }

            foreach (IPixelFormat desiredPixelFormat in desiredState.PixelFormat)
            {
                IPixelFormat pf = desiredPixelFormat;
                if (desiredPixelFormat is PixelFormatNv12 nv12)
                {
                    foreach (IPixelFormat availablePixelFormat in AvailablePixelFormats.ForPixelFormat(nv12.Name, null))
                    {
                        pf = availablePixelFormat;
                    }
                }

                var watermarkFilter = new OverlayWatermarkFilter(
                    watermark.DesiredState,
                    desiredState.PaddedSize,
                    videoStream.SquarePixelFrameSize(currentState.PaddedSize),
                    pf,
                    _logger);
                watermarkOverlayFilterSteps.Add(watermarkFilter);
            }
        }

        return currentState;
    }

    private static FrameState SetSubtitle(
        VideoInputFile videoInputFile,
        Option<SubtitleInputFile> subtitleInputFile,
        PipelineContext context,
        bool forceSoftwareOverlay,
        FrameState currentState,
        FrameState desiredState,
        string fontsFolder,
        List<IPipelineFilterStep> subtitleOverlayFilterSteps)
    {
        foreach (SubtitleInputFile subtitle in subtitleInputFile)
        {
            if (context.HasSubtitleText)
            {
                // if (videoInputFile.FilterSteps.Count == 0 && videoInputFile.InputOptions.OfType<CuvidDecoder>().Any())
                // {
                //     // change the hw accel output to software so the explicit download isn't needed
                //     foreach (CuvidDecoder decoder in videoInputFile.InputOptions.OfType<CuvidDecoder>())
                //     {
                //         decoder.HardwareAccelerationMode = HardwareAccelerationMode.None;
                //     }
                // }
                // else
                // {
                var downloadFilter = new HardwareDownloadFilter(currentState);
                currentState = downloadFilter.NextState(currentState);
                videoInputFile.FilterSteps.Add(downloadFilter);
                // }

                var subtitlesFilter = new SubtitlesFilter(fontsFolder, subtitle);
                currentState = subtitlesFilter.NextState(currentState);
                videoInputFile.FilterSteps.Add(subtitlesFilter);
            }
            else if (context.HasSubtitleOverlay)
            {
                var pixelFormatFilter = new VaapiSubtitlePixelFormatFilter();
                subtitle.FilterSteps.Add(pixelFormatFilter);

                if (forceSoftwareOverlay)
                {
                    var downloadFilter = new HardwareDownloadFilter(currentState);
                    currentState = downloadFilter.NextState(currentState);
                    videoInputFile.FilterSteps.Add(downloadFilter);

                    foreach (IPixelFormat pixelFormat in desiredState.PixelFormat)
                    {
                        IPixelFormat pf = pixelFormat;
                        if (pixelFormat is PixelFormatNv12 nv12)
                        {
                            foreach (IPixelFormat format in AvailablePixelFormats.ForPixelFormat(nv12.Name, null))
                            {
                                pf = format;
                            }
                        }

                        // only scale if scaling or padding was used for main video stream
                        if (videoInputFile.FilterSteps.Any(s => s is ScaleFilter or ScaleVaapiFilter or PadFilter))
                        {
                            var scaleFilter = new ScaleImageFilter(desiredState.PaddedSize);
                            subtitle.FilterSteps.Add(scaleFilter);
                        }

                        foreach (FrameSize croppedSize in currentState.CroppedSize)
                        {
                            var cropStep = new CropFilter(
                                currentState with { FrameDataLocation = FrameDataLocation.Software },
                                croppedSize);
                            subtitle.FilterSteps.Add(cropStep);
                        }

                        var subtitlesFilter = new OverlaySubtitleFilter(pf);
                        subtitleOverlayFilterSteps.Add(subtitlesFilter);
                    }
                }
                else
                {
                    // only scale if scaling or padding was used for main video stream
                    if (videoInputFile.FilterSteps.Any(s => s is ScaleFilter or ScaleVaapiFilter or PadFilter))
                    {
                        var scaleFilter = new ScaleSubtitleImageFilter(desiredState.PaddedSize);
                        subtitle.FilterSteps.Add(scaleFilter);
                    }

                    foreach (FrameSize croppedSize in currentState.CroppedSize)
                    {
                        var cropStep = new CropFilter(
                            currentState with { FrameDataLocation = FrameDataLocation.Software },
                            croppedSize);
                        subtitle.FilterSteps.Add(cropStep);
                    }

                    var subtitleHardwareUpload = new HardwareUploadVaapiFilter(false);
                    subtitle.FilterSteps.Add(subtitleHardwareUpload);

                    var subtitlesFilter = new OverlaySubtitleVaapiFilter();
                    subtitleOverlayFilterSteps.Add(subtitlesFilter);
                }

                if (context.HasWatermark && !forceSoftwareOverlay)
                {
                    // download for watermark
                    var hardwareDownload = new HardwareDownloadFilter(currentState);
                    currentState = hardwareDownload.NextState(currentState);
                    subtitleOverlayFilterSteps.Add(hardwareDownload);
                }
            }
        }

        return currentState;
    }

    private static void SetGraphicsEngine(
        Option<GraphicsEngineInput> graphicsEngineInput,
        FrameState currentState,
        FrameState desiredState,
        List<IPipelineFilterStep> graphicsEngineOverlayFilterSteps)
    {
        foreach (GraphicsEngineInput graphicsEngine in graphicsEngineInput)
        {
            foreach (IPixelFormat desiredPixelFormat in desiredState.PixelFormat)
            {
                IPixelFormat pf = desiredPixelFormat;
                if (desiredPixelFormat is PixelFormatNv12 nv12)
                {
                    foreach (IPixelFormat availablePixelFormat in AvailablePixelFormats.ForPixelFormat(nv12.Name, null))
                    {
                        pf = availablePixelFormat;
                    }
                }

                if (currentState.FrameDataLocation is FrameDataLocation.Hardware)
                {
                    graphicsEngine.FilterSteps.Add(new HardwareUploadVaapiFilter(false));
                }

                graphicsEngineOverlayFilterSteps.Add(new OverlayGraphicsEngineVaapiFilter(currentState, pf));
            }
        }
    }

    private static FrameState SetPad(
        VideoInputFile videoInputFile,
        FrameState desiredState,
        FrameState currentState,
        bool isHdrTonemap)
    {
        if (desiredState.CroppedSize.IsNone && currentState.PaddedSize != desiredState.PaddedSize)
        {
            if (desiredState.PadMode is FFmpegFilterMode.Software || isHdrTonemap)
            {
                var padStep = new PadFilter(currentState, desiredState.PaddedSize);
                currentState = padStep.NextState(currentState);
                videoInputFile.FilterSteps.Add(padStep);
            }
            else
            {
                var padStep = new PadVaapiFilter(currentState, desiredState.PaddedSize);
                currentState = padStep.NextState(currentState);
                videoInputFile.FilterSteps.Add(padStep);
            }
        }

        return currentState;
    }

    private static FrameState SetScale(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        PipelineContext context,
        FFmpegState ffmpegState,
        FrameState desiredState,
        FrameState currentState)
    {
        IPipelineFilterStep scaleStep;

        if (currentState.ScaledSize != desiredState.ScaledSize && ffmpegState is
            {
                DecoderHardwareAccelerationMode: HardwareAccelerationMode.None,
                EncoderHardwareAccelerationMode: HardwareAccelerationMode.None
            } && context is { HasWatermark: false, HasSubtitleOverlay: false, ShouldDeinterlace: false } ||
            ffmpegState.DecoderHardwareAccelerationMode != HardwareAccelerationMode.Vaapi)
        {
            scaleStep = new ScaleFilter(
                currentState,
                desiredState.ScaledSize,
                desiredState.PaddedSize,
                desiredState.CroppedSize,
                VideoStream.IsAnamorphicEdgeCase);
        }
        else
        {
            scaleStep = new ScaleVaapiFilter(
                currentState with
                {
                    PixelFormat = //context.HasWatermark ||
                    //context.HasSubtitleOverlay ||
                    // (desiredState.ScaledSize != desiredState.PaddedSize) ||
                    // context.HasSubtitleText ||
                    ffmpegState is
                    {
                        DecoderHardwareAccelerationMode: HardwareAccelerationMode.Nvenc,
                        EncoderHardwareAccelerationMode: HardwareAccelerationMode.None
                    }
                        ? desiredState.PixelFormat.Map(pf => (IPixelFormat)new PixelFormatNv12(pf.Name))
                        : Option<IPixelFormat>.None
                },
                desiredState.ScaledSize,
                desiredState.PaddedSize,
                desiredState.CroppedSize,
                VideoStream.IsAnamorphicEdgeCase);
        }

        if (!string.IsNullOrWhiteSpace(scaleStep.Filter))
        {
            currentState = scaleStep.NextState(currentState);
            videoInputFile.FilterSteps.Add(scaleStep);
        }

        return currentState;
    }

    private static FrameState SetDeinterlace(
        VideoInputFile videoInputFile,
        PipelineContext context,
        FFmpegState ffmpegState,
        FrameState currentState)
    {
        if (context.ShouldDeinterlace)
        {
            if (ffmpegState.DecoderHardwareAccelerationMode == HardwareAccelerationMode.Vaapi)
            {
                var filter = new DeinterlaceVaapiFilter(currentState);
                currentState = filter.NextState(currentState);
                videoInputFile.FilterSteps.Add(filter);
            }
            else
            {
                var filter = new YadifFilter(currentState);
                currentState = filter.NextState(currentState);
                videoInputFile.FilterSteps.Add(filter);
            }
        }

        return currentState;
    }

    private FrameState SetTonemap(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        FrameState currentState)
    {
        if (videoStream.ColorParams.IsHdr)
        {
            foreach (IPixelFormat pixelFormat in desiredState.PixelFormat)
            {
                if (ffmpegState.DecoderHardwareAccelerationMode == HardwareAccelerationMode.Vaapi
                    && ffmpegState.VaapiDriver == "iHD"
                    && _ffmpegCapabilities.HasFilter(FFmpegKnownFilter.TonemapOpenCL))
                {
                    var filter = new TonemapVaapiFilter(ffmpegState);
                    currentState = filter.NextState(currentState);
                    videoStream.ResetColorParams(ColorParams.Default);
                    videoInputFile.FilterSteps.Add(filter);
                }
                else
                {
                    var filter = new TonemapFilter(ffmpegState, currentState, pixelFormat);
                    currentState = filter.NextState(currentState);
                    videoStream.ResetColorParams(ColorParams.Default);
                    videoInputFile.FilterSteps.Add(filter);
                }
            }
        }

        return currentState;
    }
}
