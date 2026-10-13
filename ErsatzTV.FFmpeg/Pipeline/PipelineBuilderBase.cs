using System.Diagnostics;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Decoder;
using ErsatzTV.FFmpeg.Encoder;
using ErsatzTV.FFmpeg.Environment;
using ErsatzTV.FFmpeg.Filter;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.GlobalOption;
using ErsatzTV.FFmpeg.InputOption;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.FFmpeg.OutputOption;
using ErsatzTV.FFmpeg.OutputOption.Metadata;
using ErsatzTV.FFmpeg.Protocol;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.FFmpeg.Pipeline;

public abstract class PipelineBuilderBase : IPipelineBuilder
{
    private readonly Option<AudioInputFile> _audioInputFile;
    private readonly Option<ConcatInputFile> _concatInputFile;
    private readonly IFFmpegCapabilities _ffmpegCapabilities;
    private readonly string _fontsFolder;
    private readonly Option<GraphicsEngineInput> _graphicsEngineInput;
    private readonly HardwareAccelerationMode _hardwareAccelerationMode;
    private readonly ILogger _logger;
    private readonly string _reportsFolder;
    private readonly Option<SubtitleInputFile> _subtitleInputFile;
    private readonly Option<VideoInputFile> _videoInputFile;
    private readonly Option<WatermarkInputFile> _watermarkInputFile;

    protected PipelineBuilderBase(
        IFFmpegCapabilities ffmpegCapabilities,
        HardwareAccelerationMode hardwareAccelerationMode,
        Option<VideoInputFile> videoInputFile,
        Option<AudioInputFile> audioInputFile,
        Option<WatermarkInputFile> watermarkInputFile,
        Option<SubtitleInputFile> subtitleInputFile,
        Option<ConcatInputFile> concatInputFile,
        Option<GraphicsEngineInput> graphicsEngineInput,
        string reportsFolder,
        string fontsFolder,
        ILogger logger)
    {
        _ffmpegCapabilities = ffmpegCapabilities;
        _hardwareAccelerationMode = hardwareAccelerationMode;
        _videoInputFile = videoInputFile;
        _audioInputFile = audioInputFile;
        _watermarkInputFile = watermarkInputFile;
        _subtitleInputFile = subtitleInputFile;
        _concatInputFile = concatInputFile;
        _graphicsEngineInput = graphicsEngineInput;
        _reportsFolder = reportsFolder;
        _fontsFolder = fontsFolder;
        _logger = logger;
    }

    public FFmpegPipeline Resize(string outputFile, FrameSize scaledSize)
    {
        var pipelineSteps = new List<IPipelineStep>
        {
            new NoStandardInputOption(),
            new HideBannerOption(),
            new NoStatsOption(),
            new LoglevelErrorOption()
        };

        IPipelineFilterStep scaleStep = new ScaleImageFilter(scaledSize);
        _videoInputFile.Iter(f => f.FilterSteps.Add(scaleStep));

        pipelineSteps.Add(new VideoFilter([scaleStep]));
        pipelineSteps.Add(scaleStep);
        pipelineSteps.Add(new FileNameOutputOption(outputFile));

        return new FFmpegPipeline(pipelineSteps, false);
    }

    public FFmpegPipeline Seek(string inputFile, string codec, TimeSpan seek)
    {
        IPipelineStep outputFormat = Path.GetExtension(inputFile).ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => new OutputFormatAss(),
            ".vtt" => new OutputFormatWebVtt(),
            _ when codec.ToLowerInvariant() is "ass" or "ssa" => new OutputFormatAss(),
            _ when codec.ToLowerInvariant() is "vtt" => new OutputFormatWebVtt(),
            _ => new OutputFormatSrt()
        };

        var pipelineSteps = new List<IPipelineStep>
        {
            new NoStandardInputOption(),
            new HideBannerOption(),
            new NoStatsOption(),
            new LoglevelErrorOption(),
            new StreamSeekFilterOption(seek),
            new EncoderCopySubtitle(),
            outputFormat,
            new PipeProtocol()
        };

        return new FFmpegPipeline(pipelineSteps, false);
    }

    public FFmpegPipeline Concat(ConcatInputFile concatInputFile, FFmpegState ffmpegState)
    {
        var pipelineSteps = new List<IPipelineStep>
        {
            new NoStandardInputOption(),
            new HideBannerOption(),
            new NoStatsOption(),
            new LoglevelErrorOption(),
            new StandardFormatFlags(),
            new NoDemuxDecodeDelayOutputOption(),
            new FastStartOutputOption(),
            new ClosedGopOutputOption()
        };

        concatInputFile.AddOption(new ConcatInputFormat());
        concatInputFile.AddOption(new ReadrateInputOption(1.0));
        concatInputFile.AddOption(new InfiniteLoopInputOption(HardwareAccelerationMode.None));

        foreach (int threadCount in ffmpegState.ThreadCount)
        {
            pipelineSteps.Insert(0, new ThreadCountOption(threadCount));
        }

        pipelineSteps.Add(new NoSceneDetectOutputOption(0));
        pipelineSteps.Add(new EncoderCopyAll());

        if (ffmpegState.DoNotMapMetadata)
        {
            pipelineSteps.Add(new DoNotMapMetadataOutputOption());
        }

        pipelineSteps.AddRange(
            ffmpegState.MetadataServiceProvider.Map(sp => new MetadataServiceProviderOutputOption(sp)));

        pipelineSteps.AddRange(ffmpegState.MetadataServiceName.Map(sn => new MetadataServiceNameOutputOption(sn)));

        pipelineSteps.Add(new OutputFormatMpegTs());
        pipelineSteps.Add(new PipeProtocol());

        if (ffmpegState.SaveReport)
        {
            pipelineSteps.Add(new FFReportVariable(_reportsFolder, concatInputFile));
        }

        return new FFmpegPipeline(pipelineSteps, false);
    }

    public FFmpegPipeline WrapSegmenter(ConcatInputFile concatInputFile, FFmpegState ffmpegState)
    {
        var pipelineSteps = new List<IPipelineStep>
        {
            new NoStandardInputOption(),
            new ThreadCountOption(1),
            new HideBannerOption(),
            new LoglevelErrorOption(),
            new NoStatsOption(),
            new StandardFormatFlags(),
            new MapAllStreamsOutputOption(),
            new EncoderCopyAll()
        };

        // ffmpeg below 8 doesn't detect this without an explicit decoder
        foreach (string _ in concatInputFile.AudioFormat.Where(af => af == AudioFormat.AacLatm))
        {
            concatInputFile.AddOption(new DecoderAacLatm());
        }

        // the burst fills client buffers (e.g. plex live tv) without waiting on realtime;
        // it must stay below how far the hls session publishes ahead of wall clock (next: 12s)
        concatInputFile.AddOption(new ReadrateInputOption(1.0, 6));

        SetMetadataServiceProvider(ffmpegState, pipelineSteps);
        SetMetadataServiceName(ffmpegState, pipelineSteps);

        pipelineSteps.Add(new OutputFormatMpegTs(false));
        pipelineSteps.Add(new PipeProtocol());

        // if (ffmpegState.SaveReport)
        // {
        //     pipelineSteps.Add(new FFReportVariable(_reportsFolder, concatInputFile));
        // }

        return new FFmpegPipeline(pipelineSteps, false);
    }

    public FFmpegPipeline Build(FFmpegState ffmpegState, FrameState desiredState)
    {
        OutputOption.OutputOption outputOption = new FastStartOutputOption();

        var isFmp4Hls = false;
        if (ffmpegState.OutputFormat is OutputFormatKind.Hls or OutputFormatKind.HlsMp4)
        {
            foreach (string segmentTemplate in ffmpegState.HlsSegmentTemplate)
            {
                isFmp4Hls = segmentTemplate.Contains("m4s");
            }
        }

        if (ffmpegState.OutputFormat == OutputFormatKind.Mp4 && desiredState.VideoFormat == VideoFormat.Copy)
        {
            outputOption = new HlsDirectMp4OutputOptions();
        }
        else if (isFmp4Hls)
        {
            outputOption = new Mp4OutputOptions();
        }

        var pipelineSteps = new List<IPipelineStep>
        {
            new NoStandardInputOption(),
            new HideBannerOption(),
            new NoStatsOption(),
            new ProgressOption(),
            new LoglevelErrorOption(),
            new StandardFormatFlags(),
            new NoDemuxDecodeDelayOutputOption(),
            outputOption
        };

        if (desiredState.VideoFormat != VideoFormat.Copy)
        {
            pipelineSteps.Add(new ClosedGopOutputOption());
        }

        if (desiredState.VideoFormat != VideoFormat.Copy && !desiredState.AllowBFrames)
        {
            pipelineSteps.Add(new NoBFramesOutputOption());
        }

        foreach (ConcatInputFile concatInputFile in _concatInputFile)
        {
            concatInputFile.AddOption(new ConcatInputFormat());
            concatInputFile.AddOption(new InfiniteLoopInputOption(HardwareAccelerationMode.None));
        }

        Debug.Assert(_videoInputFile.IsSome, "Pipeline builder requires exactly one video input file");
        VideoInputFile videoInputFile = _videoInputFile.Head();

        var allVideoStreams = _videoInputFile.SelectMany(f => f.VideoStreams).ToList();
        Debug.Assert(allVideoStreams.Count == 1, "Pipeline builder requires exactly one video stream");
        VideoStream videoStream = allVideoStreams.Head();

        foreach (GraphicsEngineInput graphicsEngineInput in _graphicsEngineInput)
        {
            var targetSize = desiredState.CroppedSize.IfNone(desiredState.PaddedSize);

            graphicsEngineInput.AddOption(
                new RawVideoInputOption(
                    PixelFormat.BGRA,
                    targetSize,
                    desiredState.FrameRate.IfNone(videoStream.FrameRate.IfNone(FrameRate.DefaultFrameRate))));
        }

        var context = new PipelineContext(
            _hardwareAccelerationMode,
            _graphicsEngineInput.IsSome,
            _watermarkInputFile.IsSome,
            _subtitleInputFile.Map(s => s is { IsImageBased: true, Method: SubtitleMethod.Burn }).IfNone(false),
            _subtitleInputFile.Map(s => s is { IsImageBased: false, Method: SubtitleMethod.Burn }).IfNone(false),
            desiredState.Deinterlaced && videoStream.ScanKind is ScanKind.Interlaced,
            desiredState.BitDepth == 10,
            false,
            videoStream.ColorParams.IsHdr);

        SetSceneDetect(videoStream, ffmpegState, desiredState, pipelineSteps);
        SetFFReport(ffmpegState, pipelineSteps);
        SetStreamSeek(ffmpegState, videoInputFile);
        SetTimeLimit(ffmpegState, pipelineSteps);

        (FilterChain filterChain, ffmpegState) = BuildVideoPipeline(
            isFmp4Hls,
            videoInputFile,
            videoStream,
            ffmpegState,
            desiredState,
            context,
            pipelineSteps);

        SetThreadCount(ffmpegState, pipelineSteps);

        // don't double input files for concat segmenter (v2) parent or child
        if (_concatInputFile.IsNone && ffmpegState.OutputFormat is not OutputFormatKind.Nut)
        {
            context = context with { IsIntelVaapiOrQsv = IsIntelVaapiOrQsv(ffmpegState) };
        }

        if (_audioInputFile.IsNone)
        {
            pipelineSteps.Add(new EncoderCopyAudio());
        }
        else
        {
            foreach (AudioInputFile audioInputFile in _audioInputFile)
            {
                BuildAudioPipeline(ffmpegState, audioInputFile, pipelineSteps);
            }
        }

        SetDoNotMapMetadata(ffmpegState, pipelineSteps);
        SetMetadataServiceProvider(ffmpegState, pipelineSteps);
        SetMetadataServiceName(ffmpegState, pipelineSteps);
        SetMetadataAudioLanguage(ffmpegState, pipelineSteps);
        SetMetadataSubtitle(ffmpegState, pipelineSteps);

        if (_concatInputFile.IsSome)
        {
            foreach (string segmentTemplate in ffmpegState.HlsSegmentTemplate)
            {
                foreach (string playlistPath in ffmpegState.HlsPlaylistPath)
                {
                    pipelineSteps.Add(new OutputFormatConcatHls(segmentTemplate, playlistPath));
                }
            }
        }
        else
        {
            SetOutputFormat(ffmpegState, desiredState, pipelineSteps, videoStream);
        }

        var complexFilter = new ComplexFilter(
            _videoInputFile,
            _audioInputFile,
            _watermarkInputFile,
            _subtitleInputFile,
            _graphicsEngineInput,
            context,
            filterChain);

        pipelineSteps.Add(complexFilter);

        return new FFmpegPipeline(pipelineSteps, context.IsIntelVaapiOrQsv);
    }

    private void LogUnknownDecoder(
        HardwareAccelerationMode hardwareAccelerationMode,
        string videoFormat,
        string pixelFormat) =>
        _logger.LogWarning(
            "Unable to determine decoder for {AccelMode} - {VideoFormat} - {PixelFormat}; may have playback issues",
            hardwareAccelerationMode,
            videoFormat,
            pixelFormat);

    private Option<IEncoder> LogUnknownEncoder(HardwareAccelerationMode hardwareAccelerationMode, string videoFormat)
    {
        _logger.LogWarning(
            "Unable to determine video encoder for {AccelMode} - {VideoFormat}; may have playback issues",
            hardwareAccelerationMode,
            videoFormat);
        return Option<IEncoder>.None;
    }

    private static void SetOutputFormat(
        FFmpegState ffmpegState,
        FrameState desiredState,
        List<IPipelineStep> pipelineSteps,
        VideoStream videoStream)
    {
        switch (ffmpegState.OutputFormat)
        {
            case OutputFormatKind.Mkv:
                pipelineSteps.Add(new OutputFormatMkv());
                pipelineSteps.Add(new PipeProtocol());
                break;
            case OutputFormatKind.MpegTs:
                pipelineSteps.Add(new OutputFormatMpegTs());
                pipelineSteps.Add(new PipeProtocol());
                break;
            case OutputFormatKind.Nut:
                // mkv doesn't want to store rawvideo with yuv420p10le, so we have to use NUT
                if (desiredState.BitDepth > 8)
                {
                    pipelineSteps.Add(new OutputFormatNut());
                }
                else
                {
                    // yuv420p seems to work better with mkv (NUT results in duplicate PTS)
                    pipelineSteps.Add(new OutputFormatMkv());
                }

                pipelineSteps.Add(new PipeProtocol());
                break;
            case OutputFormatKind.Mp4:
                pipelineSteps.Add(new OutputFormatMp4());
                pipelineSteps.Add(new PipeProtocol());
                break;
            case OutputFormatKind.Hls:
            case OutputFormatKind.HlsMp4:
                foreach (string playlistPath in ffmpegState.HlsPlaylistPath)
                {
                    foreach (string segmentTemplate in ffmpegState.HlsSegmentTemplate)
                    {
                        bool oneSecondGop = ffmpegState.EncoderHardwareAccelerationMode is HardwareAccelerationMode.Qsv;

                        pipelineSteps.Add(
                            new OutputFormatHls(
                                desiredState,
                                videoStream.FrameRate,
                                ffmpegState.OutputFormat,
                                ffmpegState.HlsSegmentOptions,
                                segmentTemplate,
                                ffmpegState.HlsInitTemplate,
                                playlistPath,
                                ffmpegState.PtsOffset == TimeSpan.Zero,
                                oneSecondGop,
                                ffmpegState.IsTroubleshooting));
                    }
                }

                break;
        }
    }

    private static void SetMetadataAudioLanguage(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        foreach (string desiredAudioLanguage in ffmpegState.MetadataAudioLanguage)
        {
            pipelineSteps.Add(new MetadataAudioLanguageOutputOption(desiredAudioLanguage));
        }
    }

    private static void SetMetadataSubtitle(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        foreach (string desiredSubtitleLanguage in ffmpegState.MetadataSubtitleLanguage)
        {
            pipelineSteps.Add(new MetadataSubtitleLanguageOutputOption(desiredSubtitleLanguage));
        }

        foreach (string desiredSubtitleTitle in ffmpegState.MetadataSubtitleTitle)
        {
            pipelineSteps.Add(new MetadataSubtitleTitleOutputOption(desiredSubtitleTitle));
        }
    }

    private static void SetMetadataServiceName(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        foreach (string desiredServiceName in ffmpegState.MetadataServiceName)
        {
            pipelineSteps.Add(new MetadataServiceNameOutputOption(desiredServiceName));
        }
    }

    private static void SetMetadataServiceProvider(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        foreach (string desiredServiceProvider in ffmpegState.MetadataServiceProvider)
        {
            pipelineSteps.Add(new MetadataServiceProviderOutputOption(desiredServiceProvider));
        }
    }

    private static void SetDoNotMapMetadata(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        if (ffmpegState.DoNotMapMetadata)
        {
            pipelineSteps.Add(new DoNotMapMetadataOutputOption());
        }
    }

    private void BuildAudioPipeline(
        FFmpegState ffmpegState,
        AudioInputFile audioInputFile,
        List<IPipelineStep> pipelineSteps)
    {
        // workaround for seeking with dts; certain seeks will change format when decoding (e.g. s16p to s32p)
        foreach (TimeSpan _ in ffmpegState.Start.Filter(s => s > TimeSpan.Zero))
        {
            if (audioInputFile.Streams.OfType<AudioStream>().Any(s => s.Codec == AudioFormat.Dts))
            {
                audioInputFile.AddOption(new DecoderDtsCoreOnly());
            }
        }

        // workaround for ac3 audio layout changes
        // downmix from decoder so filter doesn't need to reinit (it can't, it will fail)
        // this only really works when the desired output format matches the minimal input format
        foreach (var audioInputStream in audioInputFile.Streams.OfType<AudioStream>()
                     .Where(s => s.Codec == AudioFormat.Ac3).HeadOrNone())
        {
            foreach (int desiredAudioChannels in audioInputFile.DesiredState.AudioChannels)
            {
                audioInputFile.AddOption(new DecoderAc3Downmix(audioInputStream.Channels, desiredAudioChannels));
            }
        }

        // always need to specify audio codec so ffmpeg doesn't default to a codec we don't want
        foreach (IEncoder step in AvailableEncoders.ForAudioFormat(ffmpegState, audioInputFile.DesiredState, _logger))
        {
            pipelineSteps.Add(step);
        }

        SetAudioChannels(audioInputFile, pipelineSteps);

        if (ffmpegState.OutputFormat is not OutputFormatKind.Nut)
        {
            SetAudioBitrate(audioInputFile, pipelineSteps);
            SetAudioBufferSize(audioInputFile, pipelineSteps);
            SetAudioSampleRate(audioInputFile, pipelineSteps);
        }

        SetAudioLoudness(audioInputFile);
        SetAudioPad(audioInputFile, pipelineSteps);
    }

    private static void SetAudioPad(AudioInputFile audioInputFile, List<IPipelineStep> pipelineSteps)
    {
        if (pipelineSteps.All(ps => ps is not EncoderCopyAudio))
        {
            audioInputFile.FilterSteps.Add(new AudioResampleFilter());
        }

        if (audioInputFile.DesiredState.PadAudio)
        {
            audioInputFile.FilterSteps.Add(new AudioPadFilter());
        }
    }

    private void SetAudioLoudness(AudioInputFile audioInputFile)
    {
        if (audioInputFile.DesiredState.AudioFormat == AudioFormat.Copy)
        {
            return;
        }

        if (audioInputFile.DesiredState.NormalizeLoudnessFilter is not AudioFilter.None)
        {
            var filter = new NormalizeLoudnessFilter(
                audioInputFile.DesiredState.NormalizeLoudnessFilter,
                audioInputFile.DesiredState.TargetLoudness,
                audioInputFile.DesiredState.AudioSampleRate);

            _audioInputFile.Iter(f => f.FilterSteps.Add(filter));
        }
    }

    private static void SetAudioSampleRate(AudioInputFile audioInputFile, List<IPipelineStep> pipelineSteps)
    {
        if (audioInputFile.DesiredState.AudioFormat == AudioFormat.Copy)
        {
            return;
        }

        foreach (int desiredSampleRate in audioInputFile.DesiredState.AudioSampleRate)
        {
            pipelineSteps.Add(new AudioSampleRateOutputOption(desiredSampleRate));
        }
    }

    private static void SetAudioBufferSize(AudioInputFile audioInputFile, List<IPipelineStep> pipelineSteps)
    {
        if (audioInputFile.DesiredState.AudioFormat == AudioFormat.Copy)
        {
            return;
        }

        foreach (int desiredBufferSize in audioInputFile.DesiredState.AudioBufferSize)
        {
            pipelineSteps.Add(new AudioBufferSizeOutputOption(desiredBufferSize));
        }
    }

    private static void SetAudioBitrate(AudioInputFile audioInputFile, List<IPipelineStep> pipelineSteps)
    {
        if (audioInputFile.DesiredState.AudioFormat == AudioFormat.Copy)
        {
            return;
        }

        foreach (int desiredBitrate in audioInputFile.DesiredState.AudioBitrate)
        {
            pipelineSteps.Add(new AudioBitrateOutputOption(desiredBitrate));
        }
    }

    private static void SetAudioChannels(AudioInputFile audioInputFile, List<IPipelineStep> pipelineSteps)
    {
        if (audioInputFile.DesiredState.AudioFormat == AudioFormat.Copy)
        {
            return;
        }

        foreach (AudioStream audioStream in audioInputFile.AudioStreams.HeadOrNone())
        {
            foreach (int desiredAudioChannels in audioInputFile.DesiredState.AudioChannels)
            {
                pipelineSteps.Add(
                    new AudioChannelsOutputOption(
                        audioInputFile.DesiredState.AudioFormat,
                        audioStream.Channels,
                        desiredAudioChannels));
            }
        }
    }

    protected abstract bool IsIntelVaapiOrQsv(FFmpegState ffmpegState);

    protected abstract FFmpegState SetAccelState(
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        PipelineContext context,
        ICollection<IPipelineStep> pipelineSteps);

    private FilterChainAndState BuildVideoPipeline(
        bool isFmp4Hls,
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        PipelineContext context,
        List<IPipelineStep> pipelineSteps)
    {
        foreach (SubtitleInputFile subtitleInputFile in _subtitleInputFile)
        {
            if (subtitleInputFile.Method == SubtitleMethod.Copy)
            {
                pipelineSteps.Add(new EncoderCopySubtitle());
            }
            else if (subtitleInputFile.Method == SubtitleMethod.Convert)
            {
                if (subtitleInputFile.IsImageBased)
                {
                    pipelineSteps.Add(new EncoderDvdSubtitle());
                }
            }
        }

        ffmpegState = SetAccelState(videoStream, ffmpegState, desiredState, context, pipelineSteps);

        // don't use explicit decoder with HLS Direct
        Option<IDecoder> maybeDecoder = desiredState.VideoFormat == VideoFormat.Copy
            ? None
            : SetDecoder(videoInputFile, videoStream, ffmpegState, context);

        Option<IPixelFormat> videoStreamPixelFormat = videoStream.PixelFormat;

        // bt.2020 needs to immediately change to bt709 and a new format
        if (videoStream.ColorParams.IsBt2020Ten)
        {
            IPixelFormat targetFormat = desiredState.PixelFormat.IfNone(new PixelFormatYuv420P());

            videoInputFile.FilterSteps.Add(new ColorspaceFilter(desiredState, videoStream, targetFormat));

            // update pipeline to have accurate color params and pixel format
            videoStream.ResetColorParams(ColorParams.Default);
            videoStreamPixelFormat = Some(targetFormat);
        }

        //SetStillImageInfiniteLoop(videoInputFile, videoStream, ffmpegState);
        SetRealtimeInput(videoInputFile, desiredState);
        SetInfiniteLoop(videoInputFile, videoStream, ffmpegState, desiredState);
        SetFrameRateOutput(desiredState, pipelineSteps);
        SetVideoTrackTimescaleOutput(desiredState, pipelineSteps);

        if (ffmpegState.OutputFormat is not OutputFormatKind.Nut)
        {
            SetVideoBitrateOutput(desiredState, pipelineSteps);
            SetVideoBufferSizeOutput(desiredState, pipelineSteps);
        }

        FilterChain filterChain = SetVideoFilters(
            videoInputFile,
            videoStream with { PixelFormat = videoStreamPixelFormat },
            _watermarkInputFile,
            _subtitleInputFile,
            _graphicsEngineInput,
            context,
            maybeDecoder,
            ffmpegState,
            desiredState,
            _fontsFolder,
            pipelineSteps);

        SetOutputTsOffset(isFmp4Hls, ffmpegState, desiredState, pipelineSteps);

        return new FilterChainAndState(filterChain, ffmpegState);
    }

    protected abstract Option<IDecoder> SetDecoder(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        PipelineContext context);

    protected Option<IDecoder> GetSoftwareDecoder(VideoStream videoStream)
    {
        Option<IDecoder> maybeDecoder = _ffmpegCapabilities.SoftwareDecoderForVideoFormat(videoStream.Codec);
        if (maybeDecoder.IsNone)
        {
            LogUnknownDecoder(
                HardwareAccelerationMode.None,
                videoStream.Codec,
                videoStream.PixelFormat.Match(pf => pf.Name, () => string.Empty));
        }

        return maybeDecoder;
    }

    protected Option<IEncoder> GetSoftwareEncoder(
        FFmpegState ffmpegState,
        FrameState currentState,
        FrameState desiredState)
    {
        if (ffmpegState.OutputFormat is OutputFormatKind.Nut)
        {
            return new EncoderRawVideo();
        }

        return desiredState.VideoFormat switch
        {
            VideoFormat.Hevc => new EncoderLibx265(
                currentState with { FrameDataLocation = FrameDataLocation.Software },
                desiredState.VideoPreset),
            VideoFormat.H264 => new EncoderLibx264(desiredState.VideoProfile, desiredState.VideoPreset),
            VideoFormat.Mpeg2Video => new EncoderMpeg2Video(),

            VideoFormat.Av1 => throw new NotSupportedException("AV1 software encoding is not supported"),

            VideoFormat.Copy => new EncoderCopyVideo(),
            VideoFormat.Undetermined => new EncoderImplicitVideo(),

            _ => LogUnknownEncoder(HardwareAccelerationMode.None, desiredState.VideoFormat)
        };
    }

    protected abstract FilterChain SetVideoFilters(
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
        ICollection<IPipelineStep> pipelineSteps);

    protected static FrameState SetCrop(
        VideoInputFile videoInputFile,
        FrameState desiredState,
        FrameState currentState)
    {
        foreach (FrameSize croppedSize in currentState.CroppedSize)
        {
            var cropStep = new CropFilter(currentState, croppedSize);
            currentState = cropStep.NextState(currentState);
            videoInputFile.FilterSteps.Add(cropStep);
        }

        return currentState;
    }

    private static void SetOutputTsOffset(
        bool isFmp4Hls,
        FFmpegState ffmpegState,
        FrameState desiredState,
        List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        if (ffmpegState.PtsOffset > TimeSpan.Zero && !isFmp4Hls)
        {
            pipelineSteps.Add(new OutputTsOffsetOption(ffmpegState.PtsOffset));
        }
    }

    private static void SetVideoBufferSizeOutput(FrameState desiredState, List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        foreach (int desiredBufferSize in desiredState.VideoBufferSize)
        {
            pipelineSteps.Add(new VideoBufferSizeOutputOption(desiredBufferSize));
        }
    }

    private static void SetVideoBitrateOutput(FrameState desiredState, List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        foreach (int desiredBitrate in desiredState.VideoBitrate)
        {
            pipelineSteps.Add(new VideoBitrateOutputOption(desiredBitrate));
        }
    }

    private static void SetVideoTrackTimescaleOutput(FrameState desiredState, List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        foreach (int desiredTimeScale in desiredState.VideoTrackTimeScale)
        {
            pipelineSteps.Add(new VideoTrackTimescaleOutputOption(desiredTimeScale));
        }
    }

    private static void SetFrameRateOutput(FrameState desiredState, List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        foreach (FrameRate desiredFrameRate in desiredState.FrameRate)
        {
            pipelineSteps.Add(new FrameRateOutputOption(desiredFrameRate));
        }
    }

    private void SetInfiniteLoop(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState)
    {
        if (!desiredState.InfiniteLoop)
        {
            return;
        }

        foreach (AudioInputFile audioInputFile in _audioInputFile)
        {
            audioInputFile.AddOption(new InfiniteLoopInputOption(ffmpegState.EncoderHardwareAccelerationMode));
        }

        if (!videoStream.StillImage)
        {
            videoInputFile.AddOption(new InfiniteLoopInputOption(ffmpegState.EncoderHardwareAccelerationMode));
        }
    }

    private void SetRealtimeInput(VideoInputFile videoInputFile, FrameState desiredState)
    {
        if (videoInputFile.StreamInputKind is StreamInputKind.Live || !desiredState.Realtime)
        {
            return;
        }

        double readRate = desiredState.VideoFormat == VideoFormat.Copy ? 1.0 : 1.05;
        _audioInputFile.Iter(a => a.AddOption(new ReadrateInputOption(readRate)));
        videoInputFile.AddOption(new ReadrateInputOption(readRate));
    }

    protected static void SetStillImageLoop(
        VideoInputFile videoInputFile,
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        ICollection<IPipelineStep> pipelineSteps)
    {
        if (videoStream.StillImage)
        {
            if (ffmpegState.IsSongWithProgress)
            {
                videoInputFile.FilterSteps.Add(
                    new SongProgressFilter(videoStream.FrameSize, ffmpegState.Start, ffmpegState.Finish));
            }
            else
            {
                videoInputFile.FilterSteps.Add(new LoopFilter());
            }

            if (desiredState.Realtime)
            {
                videoInputFile.FilterSteps.Add(new RealtimeFilter());
            }

            //pipelineSteps.Add(new ShortestOutputOption());
        }
    }

    private void SetThreadCount(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        if (ffmpegState.DecoderHardwareAccelerationMode != HardwareAccelerationMode.None)
        {
            _logger.LogDebug(
                "Forcing {Threads} ffmpeg decoding thread when hardware acceleration is used",
                1);

            pipelineSteps.Insert(0, new ThreadCountOption(1));
        }
        else
        {
            foreach (int threadCount in ffmpegState.ThreadCount)
            {
                pipelineSteps.Insert(0, new ThreadCountOption(threadCount));
            }
        }
    }

    private static void SetSceneDetect(
        // ReSharper disable once SuggestBaseTypeForParameter
        VideoStream videoStream,
        FFmpegState ffmpegState,
        FrameState desiredState,
        List<IPipelineStep> pipelineSteps)
    {
        if (desiredState.VideoFormat == VideoFormat.Copy)
        {
            return;
        }

        // -sc_threshold 0 is unsupported with mpeg2video
        if (videoStream.Codec == VideoFormat.Mpeg2Video || desiredState.VideoFormat == VideoFormat.Mpeg2Video ||
            ffmpegState.DecoderHardwareAccelerationMode == HardwareAccelerationMode.VideoToolbox)
        {
            pipelineSteps.Add(new NoSceneDetectOutputOption(1_000_000_000));
        }
        else
        {
            pipelineSteps.Add(new NoSceneDetectOutputOption(0));
        }
    }

    private void SetFFReport(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps)
    {
        if (ffmpegState.SaveReport)
        {
            pipelineSteps.Add(new FFReportVariable(_reportsFolder, _concatInputFile));
        }
    }

    private void SetStreamSeek(FFmpegState ffmpegState, VideoInputFile videoInputFile)
    {
        foreach (TimeSpan desiredStart in ffmpegState.Start.Filter(s => s > TimeSpan.Zero))
        {
            var option = new StreamSeekInputOption(desiredStart);
            _audioInputFile.Iter(a => a.AddOption(option));
            videoInputFile.AddOption(option);
        }
    }

    private static void SetTimeLimit(FFmpegState ffmpegState, List<IPipelineStep> pipelineSteps) =>
        pipelineSteps.AddRange(ffmpegState.Finish.Map(finish => new TimeLimitOutputOption(finish)));

    private sealed record FilterChainAndState(FilterChain FilterChain, FFmpegState FFmpegState);
}
