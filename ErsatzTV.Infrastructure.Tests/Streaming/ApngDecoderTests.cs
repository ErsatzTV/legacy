using System.Buffers.Binary;
using System.Text;
using ErsatzTV.Infrastructure.Streaming.Graphics;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Tests.Streaming;

[TestFixture]
public class ApngDecoderTests
{
    private const int Size = 4;
    private const byte DisposeNone = 0;
    private const byte DisposeBackground = 1;
    private const byte DisposePrevious = 2;
    private const byte BlendSource = 0;
    private const byte BlendOver = 1;

    private static readonly SKImageInfo CanvasInfo = new(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Green = new(0, 255, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    [Test]
    public void Should_return_no_frames_for_static_png()
    {
        using var bitmap = SolidBitmap(Size, Size, Red);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        ApngDecoder.Decode(png.ToArray(), CanvasInfo).ShouldBeEmpty();
    }

    [Test]
    public void Should_blend_over_previous_frame()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red),
            Frame(1, 1, 2, 2, Blue.WithAlpha(128), blend: BlendOver));

        List<ApngFrame> frames = Decode(apng);

        AssertPixel(frames[1], 0, 0, Red);
        AssertPixel(frames[1], 1, 1, new SKColor(127, 0, 128));
    }

    [Test]
    public void Should_replace_region_with_blend_source()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red),
            Frame(1, 1, 2, 2, SKColors.Transparent));

        List<ApngFrame> frames = Decode(apng);

        AssertPixel(frames[1], 0, 0, Red);
        AssertPixel(frames[1], 1, 1, SKColors.Transparent);
    }

    [Test]
    public void Should_clear_region_with_dispose_background()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red, DisposeBackground),
            Frame(0, 0, 1, 1, Green, blend: BlendOver));

        List<ApngFrame> frames = Decode(apng);

        AssertPixel(frames[0], 3, 3, Red);
        AssertPixel(frames[1], 0, 0, Green);
        AssertPixel(frames[1], 3, 3, SKColors.Transparent);
    }

    [Test]
    public void Should_restore_region_with_dispose_previous()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red),
            Frame(0, 0, 2, 2, Green, DisposePrevious),
            Frame(3, 3, 1, 1, Blue));

        List<ApngFrame> frames = Decode(apng);

        AssertPixel(frames[1], 0, 0, Green);
        AssertPixel(frames[2], 0, 0, Red);
        AssertPixel(frames[2], 3, 3, Blue);
    }

    [Test]
    public void Should_treat_dispose_previous_on_first_frame_as_background()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red, DisposePrevious),
            Frame(0, 0, 1, 1, Green, blend: BlendOver));

        List<ApngFrame> frames = Decode(apng);

        AssertPixel(frames[1], 3, 3, SKColors.Transparent);
    }

    [Test]
    public void Should_skip_default_image_when_not_part_of_animation()
    {
        byte[] apng = BuildApng(
            defaultImage: Blue,
            frames:
            [
                Frame(0, 0, Size, Size, Red),
                Frame(0, 0, Size, Size, Green)
            ]);

        List<ApngFrame> frames = Decode(apng);

        frames.Count.ShouldBe(2);
        AssertPixel(frames[0], 0, 0, Red);
        AssertPixel(frames[1], 0, 0, Green);
    }

    [Test]
    public void Should_read_frame_delays()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red, delayNumerator: 1, delayDenominator: 4),
            Frame(0, 0, Size, Size, Green, delayNumerator: 7, delayDenominator: 0));

        List<ApngFrame> frames = Decode(apng);

        frames[0].DelaySeconds.ShouldBe(0.25);
        frames[1].DelaySeconds.ShouldBe(0.07);
    }

    [Test]
    public void Should_return_no_frames_for_truncated_apng()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red),
            Frame(0, 0, Size, Size, Green));

        ApngDecoder.Decode(apng.AsSpan(0, apng.Length - 30), CanvasInfo).ShouldBeEmpty();
    }

    [Test]
    public void Should_return_no_frames_for_frame_outside_image()
    {
        byte[] apng = BuildApng(
            Frame(0, 0, Size, Size, Red),
            Frame(3, 3, 2, 2, Green));

        ApngDecoder.Decode(apng, CanvasInfo).ShouldBeEmpty();
    }

    private static List<ApngFrame> Decode(byte[] apng)
    {
        List<ApngFrame> frames = ApngDecoder.Decode(apng, CanvasInfo);
        frames.ShouldNotBeEmpty();
        return frames;
    }

    private static void AssertPixel(ApngFrame frame, int x, int y, SKColor expected)
    {
        SKColor actual = frame.Bitmap.GetPixel(x, y);
        if (expected.Alpha == 0)
        {
            actual.Alpha.ShouldBe((byte)0);
            return;
        }

        ((int)actual.Red).ShouldBeInRange(expected.Red - 2, expected.Red + 2);
        ((int)actual.Green).ShouldBeInRange(expected.Green - 2, expected.Green + 2);
        ((int)actual.Blue).ShouldBeInRange(expected.Blue - 2, expected.Blue + 2);
        ((int)actual.Alpha).ShouldBeInRange(expected.Alpha - 2, expected.Alpha + 2);
    }

    private static TestFrame Frame(
        int x,
        int y,
        int width,
        int height,
        SKColor color,
        byte dispose = DisposeNone,
        byte blend = BlendSource,
        ushort delayNumerator = 1,
        ushort delayDenominator = 10) =>
        new(x, y, width, height, color, dispose, blend, delayNumerator, delayDenominator);

    private static byte[] BuildApng(params TestFrame[] frames) => BuildApng(null, frames);

    private static byte[] BuildApng(SKColor? defaultImage, TestFrame[] frames)
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] ihdr = EncodeChunks(SolidBitmap(Size, Size, SKColors.Transparent)).Single(c => c.Type == "IHDR").Data;
        WriteChunk(stream, "IHDR", ihdr);

        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Length);
        WriteChunk(stream, "acTL", actl);

        var sequence = 0u;
        if (defaultImage is { } color)
        {
            WriteImageData(stream, SolidBitmap(Size, Size, color), "IDAT", ref sequence);
        }

        for (var i = 0; i < frames.Length; i++)
        {
            TestFrame frame = frames[i];
            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)frame.Width);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)frame.Height);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), (uint)frame.X);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), (uint)frame.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), frame.DelayNumerator);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), frame.DelayDenominator);
            fctl[24] = frame.Dispose;
            fctl[25] = frame.Blend;
            WriteChunk(stream, "fcTL", fctl);

            string type = i == 0 && defaultImage is null ? "IDAT" : "fdAT";
            WriteImageData(stream, SolidBitmap(frame.Width, frame.Height, frame.Color), type, ref sequence);
        }

        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteImageData(MemoryStream stream, SKBitmap bitmap, string type, ref uint sequence)
    {
        using (bitmap)
        {
            foreach ((string _, byte[] data) in EncodeChunks(bitmap).Where(c => c.Type == "IDAT"))
            {
                if (type == "IDAT")
                {
                    WriteChunk(stream, type, data);
                    continue;
                }

                var fdat = new byte[data.Length + 4];
                BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
                data.CopyTo(fdat, 4);
                WriteChunk(stream, type, fdat);
            }
        }
    }

    private static SKBitmap SolidBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(color);
        return bitmap;
    }

    private static List<(string Type, byte[] Data)> EncodeChunks(SKBitmap bitmap)
    {
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        var chunks = new List<(string, byte[])>();
        for (var position = 8; position < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            chunks.Add((Encoding.ASCII.GetString(png, position + 4, 4), png.AsSpan(position + 8, length).ToArray()));
            position += length + 12;
        }

        return chunks;
    }

    private static void WriteChunk(MemoryStream stream, string type, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), Crc32(chunk.AsSpan(4, data.Length + 4)));
        stream.Write(chunk);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private sealed record TestFrame(
        int X,
        int Y,
        int Width,
        int Height,
        SKColor Color,
        byte Dispose,
        byte Blend,
        ushort DelayNumerator,
        ushort DelayDenominator);
}
