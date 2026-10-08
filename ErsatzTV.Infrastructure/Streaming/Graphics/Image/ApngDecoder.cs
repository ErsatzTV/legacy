using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

// SKCodec only decodes the default image of an APNG, so each frame is repackaged
// as a standalone PNG for Skia and composited here per the APNG spec
internal static class ApngDecoder
{
    private const byte DisposeOpBackground = 1;
    private const byte DisposeOpPrevious = 2;
    private const byte BlendOpSource = 0;

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    // returns no frames when the image is not an APNG or cannot be decoded as one
    public static List<ApngFrame> Decode(ReadOnlySpan<byte> png, SKImageInfo canvasInfo)
    {
        List<FrameControl> frames = Parse(png, out byte[] ihdr, out List<byte[]> sharedChunks);
        if (frames.Count == 0)
        {
            return [];
        }

        var result = new List<ApngFrame>(frames.Count);
        try
        {
            using var canvasBitmap = new SKBitmap(canvasInfo);
            canvasBitmap.Erase(SKColors.Transparent);
            using var canvas = new SKCanvas(canvasBitmap);
            using var sourcePaint = new SKPaint();
            sourcePaint.BlendMode = SKBlendMode.Src;
            using var overPaint = new SKPaint();

            for (var i = 0; i < frames.Count; i++)
            {
                FrameControl frame = frames[i];
                if (frame.Width == 0 || frame.Height == 0 || frame.X + frame.Width > canvasInfo.Width ||
                    frame.Y + frame.Height > canvasInfo.Height)
                {
                    throw new InvalidDataException("APNG frame is outside of the image");
                }

                using SKBitmap frameBitmap = SKBitmap.Decode(BuildFramePng(ihdr, sharedChunks, frame))
                                            ?? throw new InvalidDataException("Failed to decode APNG frame");

                byte disposeOp = i == 0 && frame.DisposeOp == DisposeOpPrevious
                    ? DisposeOpBackground
                    : frame.DisposeOp;
                using SKBitmap previous = disposeOp == DisposeOpPrevious ? canvasBitmap.Copy() : null;

                canvas.DrawBitmap(
                    frameBitmap,
                    frame.X,
                    frame.Y,
                    SKSamplingOptions.Default,
                    frame.BlendOp == BlendOpSource ? sourcePaint : overPaint);
                canvas.Flush();

                result.Add(new ApngFrame(canvasBitmap.Copy(), frame.DelaySeconds));

                if (disposeOp == DisposeOpBackground)
                {
                    canvas.Save();
                    canvas.ClipRect(SKRect.Create(frame.X, frame.Y, frame.Width, frame.Height));
                    canvas.Clear(SKColors.Transparent);
                    canvas.Restore();
                    canvas.Flush();
                }
                else if (previous is not null)
                {
                    canvas.DrawBitmap(previous, 0, 0, SKSamplingOptions.Default, sourcePaint);
                    canvas.Flush();
                }
            }

            return result;
        }
        catch (InvalidDataException)
        {
            result.ForEach(f => f.Bitmap.Dispose());
            return [];
        }
    }

    private static List<FrameControl> Parse(ReadOnlySpan<byte> png, out byte[] ihdr, out List<byte[]> sharedChunks)
    {
        ihdr = null;
        sharedChunks = [];
        var frames = new List<FrameControl>();
        var hasAnimationControl = false;
        var sawImageData = false;

        if (!png.StartsWith(Signature))
        {
            return [];
        }

        int position = Signature.Length;
        while (position + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > png.Length - position - 12)
            {
                return [];
            }

            string type = Encoding.ASCII.GetString(png.Slice(position + 4, 4));
            ReadOnlySpan<byte> data = png.Slice(position + 8, (int)length);

            switch (type)
            {
                case "IHDR" when length == 13:
                    ihdr = data.ToArray();
                    break;
                case "acTL":
                    hasAnimationControl = true;
                    break;
                case "fcTL" when length == 26:
                    frames.Add(FrameControl.Parse(data));
                    break;
                case "fcTL":
                    return [];
                case "IDAT":
                    // the default image is only the first frame when an fcTL precedes it
                    if (!sawImageData && frames.Count == 1)
                    {
                        frames[0].IncludesDefaultImage = true;
                    }

                    if (frames.Count == 1 && frames[0].IncludesDefaultImage)
                    {
                        frames[0].Data.Add(data.ToArray());
                    }

                    sawImageData = true;
                    break;
                case "fdAT" when frames.Count > 0 && length > 4:
                    frames[^1].Data.Add(data[4..].ToArray());
                    break;
                case "fdAT":
                    return [];
                case "IEND":
                    position = png.Length;
                    continue;
                default:
                    if (!sawImageData && type != "IHDR")
                    {
                        sharedChunks.Add(png.Slice(position, (int)length + 12).ToArray());
                    }

                    break;
            }

            position += (int)length + 12;
        }

        if (!hasAnimationControl || ihdr is null || frames.Any(f => f.Data.Count == 0))
        {
            return [];
        }

        return frames;
    }

    private static byte[] BuildFramePng(byte[] ihdr, List<byte[]> sharedChunks, FrameControl frame)
    {
        using var stream = new MemoryStream();
        stream.Write(Signature);

        byte[] frameHeader = ihdr.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader, frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(4), frame.Height);
        WriteChunk(stream, "IHDR", frameHeader);

        foreach (byte[] chunk in sharedChunks)
        {
            stream.Write(chunk);
        }

        foreach (byte[] data in frame.Data)
        {
            WriteChunk(stream, "IDAT", data);
        }

        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(MemoryStream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(data);

        uint crc = UpdateCrc(0xFFFFFFFF, header[4..]);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private sealed class FrameControl
    {
        public uint Width { get; private init; }
        public uint Height { get; private init; }
        public int X { get; private init; }
        public int Y { get; private init; }
        public double DelaySeconds { get; private init; }
        public byte DisposeOp { get; private init; }
        public byte BlendOp { get; private init; }
        public bool IncludesDefaultImage { get; set; }
        public List<byte[]> Data { get; } = [];

        public static FrameControl Parse(ReadOnlySpan<byte> data)
        {
            ushort delayNumerator = BinaryPrimitives.ReadUInt16BigEndian(data[20..]);
            ushort delayDenominator = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);

            return new FrameControl
            {
                Width = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
                Height = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
                X = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[12..]), int.MaxValue),
                Y = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue),
                // a zero denominator means hundredths of a second
                DelaySeconds = delayNumerator / (double)(delayDenominator == 0 ? 100 : delayDenominator),
                DisposeOp = data[24],
                BlendOp = data[25]
            };
        }
    }
}

internal sealed record ApngFrame(SKBitmap Bitmap, double DelaySeconds);
