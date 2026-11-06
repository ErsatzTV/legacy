using System.Collections.Immutable;
using ErsatzTV.FFmpeg.Format;
using Lennox.NvEncSharp;

namespace ErsatzTV.FFmpeg.Capabilities.Nvidia;

internal static class CudaHelper
{
    public static bool IsLoaded { get; set; }

    private static bool _success;
    private static bool _initialized;
    private static readonly Lock Lock = new();

    public static Guid Av1CodecGuid = Guid.Parse("0A352289-0AA7-4759-862D-5D15CD16D254");
    public static Guid Av1ProfileGuid = Guid.Parse("5f2a39f5-f14e-4f95-9a9e-b76d568fcf97");

    private static readonly Dictionary<string, Guid> AllEncoders = new()
    {
        [VideoFormat.H264] = NvEncCodecGuids.H264,
        [VideoFormat.Hevc] = NvEncCodecGuids.Hevc,
        [VideoFormat.Av1] = Av1CodecGuid
    };

    private sealed record Decoder(CuVideoChromaFormat ChromaFormat, CuVideoCodec VideoCodec, int BitDepth);

    private static readonly Dictionary<string, Decoder> AllDecoders = new()
    {
        [$"{VideoFormat.Mpeg2Video} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.MPEG2, 8),
        [$"{VideoFormat.Mpeg2Video} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.MPEG2, 10),
        [$"{VideoFormat.Mpeg4} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.MPEG4, 8),
        [$"{VideoFormat.Mpeg4} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.MPEG4, 10),
        [$"{VideoFormat.Vc1} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VC1, 8),
        [$"{VideoFormat.Vc1} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VC1, 10),
        [$"{VideoFormat.H264} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.H264, 8),
        [$"{VideoFormat.H264} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.H264, 10),
        [$"{VideoFormat.Hevc} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.HEVC, 8),
        [$"{VideoFormat.Hevc} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.HEVC, 10),
        [$"{VideoFormat.Vp8} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VP8, 8),
        [$"{VideoFormat.Vp8} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VP8, 10),
        [$"{VideoFormat.Vp9} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VP9, 8),
        [$"{VideoFormat.Vp9} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, CuVideoCodec.VP9, 10),
        [$"{VideoFormat.Av1} 8-bit"] = new Decoder(CuVideoChromaFormat.YUV420, (CuVideoCodec)11, 8), // AV1
        [$"{VideoFormat.Av1} 10-bit"] = new Decoder(CuVideoChromaFormat.YUV420, (CuVideoCodec)11, 10) // AV1
    };

    private static bool EnsureInit()
    {
        if (_initialized)
        {
            return _success;
        }

        lock (Lock)
        {
            if (_initialized)
            {
                return _success;
            }

            try
            {
                LibNvEnc.TryInitialize(out string? error);
                if (string.IsNullOrEmpty(error))
                {
                    LibCuda.Initialize();
                    _success = true;
                }
            }
            catch (LibNvEncException)
            {
                _success = false;
            }

            _initialized = true;
        }

        return _success;
    }

    internal static Option<List<CudaDevice>> GetDevices()
    {
        var result = new List<CudaDevice>();

        if (!IsLoaded || !EnsureInit())
        {
            return Option<List<CudaDevice>>.None;
        }

        foreach (var description in CuDevice.GetDescriptions())
        {
            try
            {
                var device = description.Device;

                string name = device.GetName();
                int nullIndex = name.IndexOf('\0');
                if (nullIndex > 0)
                {
                    name = name[..nullIndex];
                }

                int major = device.GetAttribute(CuDeviceAttribute.ComputeCapabilityMajor);
                int minor = device.GetAttribute(CuDeviceAttribute.ComputeCapabilityMinor);

                using var context = device.CreateContext();
                context.Push();

                var encoders = new List<CudaCodec>();
                var decoders = new List<CudaDecoder>();

                foreach ((string decoderName, Decoder decoder) in AllDecoders)
                {
                    var caps = new CuVideoDecodeCaps
                    {
                        BitDepthMinus8 = decoder.BitDepth - 8,
                        ChromaFormat = decoder.ChromaFormat,
                        CodecType = decoder.VideoCodec
                    };

                    var decoderCaps = LibCuVideo.GetDecoderCaps(ref caps);
                    if (decoderCaps != CuResult.Success)
                    {
                        Console.WriteLine(
                            $"Failed to check decode capability for device {description.Handle} ({name}): {decoderCaps}");
                        continue;
                    }

                    if (!caps.IsSupported)
                    {
                        continue;
                    }

                    decoders.Add(new CudaDecoder(decoderName, decoder.VideoCodec, decoder.BitDepth));
                }

                var sessionParams = new NvEncOpenEncodeSessionExParams
                {
                    Version = LibNvEnc.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER,
                    ApiVersion = LibNvEnc.NVENCAPI_VERSION,
                    Device = context.Handle,
                    DeviceType = NvEncDeviceType.Cuda
                };

                var encoder = LibNvEnc.OpenEncoder(ref sessionParams);
                try
                {
                    IReadOnlyList<Guid> codecGuids = encoder.GetEncodeGuids();
                    foreach ((string codecName, Guid codecGuid) in AllEncoders)
                    {
                        if (codecGuids.Contains(codecGuid))
                        {
                            IReadOnlyList<Guid> codecProfileGuids = encoder.GetEncodeProfileGuids(codecGuid);

                            var bitDepths = new List<int> { 8 };

                            var cap = new NvEncCapsParam { CapsToQuery = NvEncCaps.Support10bitEncode };
                            var capsVal = 0;
                            encoder.GetEncodeCaps(codecGuid, ref cap, ref capsVal);
                            if (capsVal > 0)
                            {
                                bitDepths.Add(10);
                            }

                            cap = new NvEncCapsParam { CapsToQuery = NvEncCaps.SupportBframeRefMode };
                            capsVal = 0;
                            encoder.GetEncodeCaps(codecGuid, ref cap, ref capsVal);
                            bool bFrameRefMode = capsVal > 0;

                            var cudaCodec = new CudaCodec(
                                codecName,
                                codecGuid,
                                codecProfileGuids,
                                bitDepths.ToImmutableList(),
                                bFrameRefMode);

                            encoders.Add(cudaCodec);
                        }
                    }
                }
                finally
                {
                    encoder.DestroyEncoder();
                }

                result.Add(new CudaDevice(device.Handle, name, new Version(major, minor), encoders, decoders));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing CUDA device {description.Handle}: {ex}");
            }
        }

        return result;
    }
}
