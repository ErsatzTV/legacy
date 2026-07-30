using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Decoder;
using ErsatzTV.FFmpeg.Decoder.Qsv;
using ErsatzTV.FFmpeg.Encoder;
using ErsatzTV.FFmpeg.Encoder.Qsv;
using ErsatzTV.FFmpeg.Environment;
using ErsatzTV.FFmpeg.Filter;
using ErsatzTV.FFmpeg.Filter.Qsv;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.GlobalOption.HardwareAcceleration;
using ErsatzTV.FFmpeg.InputOption;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.FFmpeg.OutputOption;
using ErsatzTV.FFmpeg.State;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.FFmpeg.Pipeline;

public class QsvPipelineBuilder : SoftwarePipelineBuilder
{
    private readonly IFFmpegCapabilities _ffmpegCapabilities;
    private readonly IHardwareCapabilities _hardwareCapabilities;
    private readonly ILogger _logger;

    public QsvPipelineBuilder(
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

    protected override bool IsIntelVaapiOrQsv(FFmpegState ffmpegState) =>
        ffmpegState.DecoderHardwareAccelerationMode is HardwareAccelerationMode.Qsv ||
        ffmpegState.EncoderHardwareAccelerationMode is HardwareAccelerationMode.Qsv;

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

        bool isHevcOrH264 = videoStream.Codec is /*VideoFormat.Hevc or*/ VideoFormat.H264;
        bool is10Bit = videoStream.PixelFormat.Map(pf => pf.BitDepth).IfNone(8) == 10;

        // 10-bit hevc/h264 qsv decoders have issues, so use software
        if (decodeCapability == FFmpegCapability.Hardware && isHevcOrH264 && is10Bit)
        {
            decodeCapability = FFmpegCapability.Software;
        }

        // QSV cannot always decode properly when *not* seeking, so check if software is needed
        if (decodeCapability == FFmpegCapability.Hardware && ffmpegState.Start.Filter(s => s > TimeSpan.Zero).IsNone &&
            videoStream.HasMultipleProfiles)
        {
            _logger.LogDebug("Forcing software decode when input file uses multiple h264 profiles");
            decodeCapability = FFmpegCapability.Software;
        }

        // give a bogus value so no cuda devices are visible to ffmpeg
        pipelineSteps.Add(new CudaVisibleDevicesVariable("999"));

        pipelineSteps.Add(new QsvHardwareAccelerationOption(ffmpegState.VaapiDevice, decodeCapability));

        // disable hw accel if decoder/encoder isn't supported
        return ffmpegState with
        {
            DecoderHardwareAccelerationMode = decodeCapability == FFmpegCapability.Hardware
                ? HardwareAccelerationMode.Qsv
                : HardwareAccelerationMode.None,
            EncoderHardwareAccelerationMode = encodeCapability == FFmpegCapability.Hardware
                ? HardwareAccelerationMode.Qsv
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
            (HardwareAccelerationMode.Qsv, VideoFormat.Hevc) => new DecoderHevcQsv(),
            (HardwareAccelerationMode.Qsv, VideoFormat.H264) => new DecoderH264Qsv(),
            (HardwareAccelerationMode.Qsv, VideoFormat.Mpeg2Video) => new DecoderMpeg2Qsv(),
            (HardwareAccelerationMode.Qsv, VideoFormat.Vc1) => new DecoderVc1Qsv(),
            (HardwareAccelerationMode.Qsv, VideoFormat.Vp9) => new DecoderVp9Qsv(),
            (HardwareAccelerationMode.Qsv, VideoFormat.Av1) => new DecoderAv1Qsv(),

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

            // consider 8-bit hardware frames to be wrapped in nv12
            PixelFormat = ffmpegState.DecoderHardwareAccelerationMode == HardwareAccelerationMode.Qsv
                ? videoStream.PixelFormat.Map(pf => pf.BitDepth == 8 ? new PixelFormatNv12(pf.Name) : pf)
                : videoStream.PixelFormat,

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
            desiredState = desiredState with
            {
                PixelFormat = Some(context.Is10BitOutput ? pixelFormat : new PixelFormatNv12(pixelFormat.Name))
            };
        }

        // _logger.LogDebug("After decode: {PixelFormat}", currentState.PixelFormat);
        currentState = SetDeinterlace(videoInputFile, context, ffmpegState, currentState);
        // _logger.LogDebug("After deinterlace: {PixelFormat}", currentState.PixelFormat);
        currentState = SetScale(videoInputFile, videoStream, context, ffmpegState, desiredState, currentState);
        // _logger.LogDebug("After scale: {PixelFormat}", currentState.PixelFormat);
        bool isHdrTonemap = videoStream.ColorParams.IsHdr;
        currentState = SetTonemap(videoInputFile, videoStream, ffmpegState, desiredState, currentState);
        currentState = SetPad(videoInputFile, videoStream, ffmpegState, desiredState, currentState, isHdrTonemap);
        // _logger.LogDebug("After pad: {PixelFormat}", currentState.PixelFormat);
        currentState = SetCrop(videoInputFile, desiredState, currentState);
        SetStillImageLoop(videoInputFile, videoStream, ffmpegState, desiredState, pipelineSteps);

        // need to download for any sort of overlay (and always for setpts)
        if (currentState.FrameDataLocation == FrameDataLocation.Hardware) //&&
            //(context.HasSubtitleOverlay || context.HasWatermark || context.HasGraphicsEngine))
        {
            var hardwareDownload = new HardwareDownloadFilter(currentState);
            currentState = hardwareDownload.NextState(currentState);
            videoInputFile.FilterSteps.Add(hardwareDownload);
        }

        // use normalized fps, or source fps
        FrameRate frameRate =
            desiredState.FrameRate.IfNone(videoStream.FrameRate.IfNone(FrameRate.DefaultFrameRate));
        videoInputFile.FilterSteps.Add(new ResetPtsFilter(frameRate));

        // since fps will set frame rate, remove the output option
        foreach (var fr in pipelineSteps.OfType<FrameRateOutputOption>().HeadOrNone())
        {
            pipelineSteps.Remove(fr);
        }

        currentState = SetSubtitle(
            videoInputFile,
            subtitleInputFile,
            context,
            ffmpegState,
            currentState,
            desiredState,
            fontsFolder,
            subtitleOverlayFilterSteps);

        currentState = SetWatermark(
            videoStream,
            watermarkInputFile,
            context,
            ffmpegState,
            desiredState,
            currentState,
            watermarkOverlayFilterSteps);

        SetGraphicsEngine(graphicsEngineInput, currentState, graphicsEngineOverlayFilterSteps);

        // after everything else is done, apply the encoder
        if (pipelineSteps.OfType<IEncoder>().All(e => e.Kind != StreamKind.Video))
        {
            Option<IEncoder> maybeEncoder =
                (ffmpegState.EncoderHardwareAccelerationMode, desiredState.VideoFormat) switch
                {
                    (HardwareAccelerationMode.Qsv, VideoFormat.Av1) => new EncoderAv1Qsv(),
                    (HardwareAccelerationMode.Qsv, VideoFormat.Hevc) => new EncoderHevcQsv(desiredState.VideoPreset),
                    (HardwareAccelerationMode.Qsv, VideoFormat.H264) => new EncoderH264Qsv(
                        desiredState.VideoProfile,
                        desiredState.VideoPreset),
                    (HardwareAccelerationMode.Qsv, VideoFormat.Mpeg2Video) => new EncoderMpeg2Qsv(),

                    (_, _) => GetSoftwareEncoder(ffmpegState, currentState, desiredState)
                };

            foreach (IEncoder encoder in maybeEncoder)
            {
                pipelineSteps.Add(encoder);
                videoInputFile.FilterSteps.Add(encoder);
            }
        }

        List<IPipelineFilterStep> pixelFormatFilterSteps = SetPixelFormat(
            videoInputFile,
            videoStream,
            desiredState,
            ffmpegState,
            currentState,
            context,
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
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FrameState desiredState,
        FFmpegState ffmpegState,
        FrameState currentState,
        PipelineContext context,
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

            IPixelFormat formatForDownload = pixelFormat;

            bool usesVppQsv =
                videoInputFile.FilterSteps.Any(f =>
                    f is QsvFormatFilter or ScaleQsvFilter or DeinterlaceQsvFilter or TonemapQsvFilter or PadQsvFilter);

            // if we have no filters, check whether we need to convert pixel format
            // since qsv doesn't seem to like doing that at the encoder
            if (!videoInputFile.FilterSteps.Any(f => f is not IEncoder))
            {
                foreach (IPixelFormat currentPixelFormat in currentState.PixelFormat)
                {
                    var requiresConversion = false;

                    if (currentPixelFormat is PixelFormatNv12 nv)
                    {
                        foreach (IPixelFormat pf in AvailablePixelFormats.ForPixelFormat(nv.Name, null))
                        {
                            requiresConversion = pf.FFmpegName != format.FFmpegName;

                            if (!requiresConversion)
                            {
                                currentState = currentState with { PixelFormat = Some(pf) };
                            }
                        }
                    }
                    else
                    {
                        requiresConversion = currentPixelFormat.FFmpegName != format.FFmpegName;
                    }

                    if (requiresConversion)
                    {
                        if (currentState.FrameDataLocation == FrameDataLocation.Hardware)
                        {
                            var filter = new QsvFormatFilter(currentPixelFormat);
                            result.Add(filter);
                            currentState = filter.NextState(currentState);

                            // if we need to convert 8-bit to 10-bit, do it here
                            if (currentPixelFormat.BitDepth == 8 && context.Is10BitOutput)
                            {
                                var p010Filter = new QsvFormatFilter(new PixelFormatP010());
                                result.Add(p010Filter);
                                currentState = p010Filter.NextState(currentState);
                            }

                            usesVppQsv = true;
                        }
                    }
                }
            }

            if (desiredState.ColorsAreBt709 && (!videoStream.ColorParams.IsBt709 || usesVppQsv))
            {
                // _logger.LogDebug("Adding colorspace filter");

                // force p010/nv12 if we're still in hardware
                if (currentState.FrameDataLocation == FrameDataLocation.Hardware)
                {
                    foreach (int bitDepth in currentState.PixelFormat.Map(pf => pf.BitDepth))
                    {
                        if (bitDepth is 10 && formatForDownload is not PixelFormatYuv420P10Le)
                        {
                            formatForDownload = new PixelFormatYuv420P10Le();
                            currentState = currentState with { PixelFormat = Some(formatForDownload) };
                        }
                        else if (bitDepth is 8 && formatForDownload is not PixelFormatNv12)
                        {
                            formatForDownload = new PixelFormatNv12(formatForDownload.Name);
                            currentState = currentState with { PixelFormat = Some(formatForDownload) };
                        }
                    }
                }

                // vpp_qsv seems to strip color info, so if we use that at all, force overriding input color info
                var colorspace = new ColorspaceFilter(
                    currentState,
                    videoStream,
                    format,
                    forceInputOverrides: usesVppQsv,
                    isQsv: true);

                currentState = colorspace.NextState(currentState);
                result.Add(colorspace);
            }

            if (ffmpegState.EncoderHardwareAccelerationMode == HardwareAccelerationMode.None)
            {
                _logger.LogDebug("Using software encoder");

                if (currentState.FrameDataLocation == FrameDataLocation.Hardware)
                {
                    _logger.LogDebug("FrameDataLocation == FrameDataLocation.Hardware");

                    formatForDownload = new PixelFormatNv12(formatForDownload.Name);

                    var hardwareDownload =
                        new HardwareDownloadFilter(currentState with { PixelFormat = Some(formatForDownload) });
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

                pipelineSteps.Add(new PixelFormatOutputOption(format));
            }

            // be explicit with pixel format when feeding to concat
            if (ffmpegState.OutputFormat is OutputFormatKind.Nut)
            {
                Option<IPipelineStep> maybePixelFormat = pipelineSteps.Find(s => s is PixelFormatOutputOption);
                foreach (IPipelineStep pf in maybePixelFormat)
                {
                    pipelineSteps.Remove(pf);
                }

                pipelineSteps.Add(new PixelFormatOutputOption(format));
            }
        }

        return result;
    }

    private FrameState SetWatermark(
        VideoStream videoStream,
        Option<WatermarkInputFile> watermarkInputFile,
        PipelineContext context,
        FFmpegState ffmpegState,
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

                // overlay filter with 10-bit vp9 seems to output alpha channel, so remove it with a pixel format change
                if (videoStream.Codec == "vp9" && desiredPixelFormat.BitDepth == 10)
                {
                    watermarkOverlayFilterSteps.Add(new PixelFormatFilter(new PixelFormatYuv420P10Le()));
                }
            }
        }

        return currentState;
    }

    private static FrameState SetSubtitle(
        VideoInputFile videoInputFile,
        Option<SubtitleInputFile> subtitleInputFile,
        PipelineContext context,
        FFmpegState ffmpegState,
        FrameState currentState,
        FrameState desiredState,
        string fontsFolder,
        List<IPipelineFilterStep> subtitleOverlayFilterSteps)
    {
        foreach (SubtitleInputFile subtitle in subtitleInputFile)
        {
            if (context.HasSubtitleText)
            {
                var downloadFilter = new HardwareDownloadFilter(currentState);
                currentState = downloadFilter.NextState(currentState);
                videoInputFile.FilterSteps.Add(downloadFilter);

                var subtitlesFilter = new SubtitlesFilter(fontsFolder, subtitle);
                currentState = subtitlesFilter.NextState(currentState);
                videoInputFile.FilterSteps.Add(subtitlesFilter);
            }
            else if (context.HasSubtitleOverlay)
            {
                IPixelFormat pixelFormat = new PixelFormatYuva420P();

                var pixelFormatFilter = new PixelFormatFilter(pixelFormat);
                subtitle.FilterSteps.Add(pixelFormatFilter);

                foreach (IPixelFormat desiredPixelFormat in desiredState.PixelFormat)
                {
                    IPixelFormat pf = desiredPixelFormat;
                    if (desiredPixelFormat is PixelFormatNv12 nv12)
                    {
                        foreach (IPixelFormat availablePixelFormat in AvailablePixelFormats.ForPixelFormat(
                                     nv12.Name,
                                     null))
                        {
                            pf = availablePixelFormat;
                        }
                    }

                    // only scale if scaling or padding was used for main video stream
                    if (videoInputFile.FilterSteps.Any(s => s is ScaleFilter or ScaleQsvFilter or PadFilter))
                    {
                        var scaleFilter = new ScaleImageFilter(desiredState.PaddedSize);
                        subtitle.FilterSteps.Add(scaleFilter);
                    }

                    var subtitlesFilter = new OverlaySubtitleFilter(pf);
                    subtitleOverlayFilterSteps.Add(subtitlesFilter);

                    // overlay filter with 10-bit vp9 seems to output alpha channel, so remove it with a pixel format change
                    if (videoInputFile.VideoStreams.Any(vs => vs.Codec == "vp9") && context.Is10BitOutput)
                    {
                        subtitleOverlayFilterSteps.Add(new PixelFormatFilter(new PixelFormatYuv420P10Le()));
                    }
                }
            }
        }

        return currentState;
    }

    private static void SetGraphicsEngine(
        Option<GraphicsEngineInput> graphicsEngineInput,
        FrameState desiredState,
        List<IPipelineFilterStep> graphicsEngineOverlayFilterSteps)
    {
        foreach (GraphicsEngineInput _ in graphicsEngineInput)
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

                graphicsEngineOverlayFilterSteps.Add(new OverlayGraphicsEngineFilter(pf));
            }
        }
    }

    private FrameState SetPad(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        FrameState currentState,
        bool isHdrTonemap)
    {
        if (desiredState.CroppedSize.IsNone && currentState.PaddedSize != desiredState.PaddedSize)
        {
            if (desiredState.PadMode is FFmpegFilterMode.Software
                || isHdrTonemap
                || !_ffmpegCapabilities.HasFilterOption(FFmpegKnownFilter.VppQsv, "pad_w"))
            {
                var padStep = new PadFilter(currentState, desiredState.PaddedSize);
                currentState = padStep.NextState(currentState);
                videoInputFile.FilterSteps.Add(padStep);
            }
            else
            {
                var padStep = new PadQsvFilter(
                    currentState,
                    desiredState.PaddedSize,
                    ffmpegState.QsvExtraHardwareFrames);
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

        bool useSoftwareFilter = ffmpegState is
        {
            DecoderHardwareAccelerationMode: HardwareAccelerationMode.None,
            EncoderHardwareAccelerationMode: HardwareAccelerationMode.None
        } && context is
            { HasGraphicsEngine: false, HasWatermark: false, HasSubtitleOverlay: false, ShouldDeinterlace: false };

        // auto_scale filter seems to muck up 10-bit software decode => hardware scale, so use software scale in that case
        useSoftwareFilter = useSoftwareFilter ||
                            ffmpegState is { DecoderHardwareAccelerationMode: HardwareAccelerationMode.None } &&
                            OperatingSystem.IsWindows() && currentState.BitDepth == 10;

        if (currentState.ScaledSize != desiredState.ScaledSize && useSoftwareFilter)
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
            scaleStep = new ScaleQsvFilter(
                currentState with
                {
                    PixelFormat = ffmpegState is
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
                ffmpegState.QsvExtraHardwareFrames,
                VideoStream.IsAnamorphicEdgeCase,
                videoStream.SampleAspectRatio);
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
            var filter = new DeinterlaceQsvFilter(currentState, ffmpegState.QsvExtraHardwareFrames);
            currentState = filter.NextState(currentState);
            videoInputFile.FilterSteps.Add(filter);
        }

        return currentState;
    }

    private static FrameState SetTonemap(
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
                if (ffmpegState.DecoderHardwareAccelerationMode == HardwareAccelerationMode.Qsv)
                {
                    var filter = new TonemapQsvFilter();
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
