using ErsatzTV.Core.Domain;
using ErsatzTV.FFmpeg.State;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public abstract class ImageElementBase : GraphicsElement, IDisposable
{
    private readonly List<double> _frameDelays = [];
    private readonly List<SKBitmap> _scaledFrames = [];
    private double _animatedDurationSeconds;
    private ushort _repeatCount;

    protected SKPointI Location { get; private set; }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        _scaledFrames?.ForEach(f => f.Dispose());
    }

    protected async Task LoadImage(
        Resolution squarePixelFrameSize,
        Resolution frameSize,
        string image,
        WatermarkLocation location,
        bool scale,
        double? scaleWidthPercent,
        double? horizontalMarginPercent,
        double? verticalMarginPercent,
        bool placeWithinSourceContent,
        CancellationToken cancellationToken)
    {
        bool isRemoteUri = Uri.TryCreate(image, UriKind.Absolute, out Uri uriResult)
                           && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

        byte[] imageBytes;
        if (isRemoteUri)
        {
            using var client = new HttpClient();
            imageBytes = await client.GetByteArrayAsync(uriResult, cancellationToken);
        }
        else
        {
            imageBytes = await File.ReadAllBytesAsync(image!, cancellationToken);
        }

        using SKData data = SKData.CreateCopy(imageBytes);
        using SKCodec codec = SKCodec.Create(data)
                              ?? throw new InvalidOperationException($"Unsupported image format: {image}");

        int sourceWidth = codec.Info.Width;
        int sourceHeight = codec.Info.Height;

        int scaledWidth = sourceWidth;
        int scaledHeight = sourceHeight;
        if (scale)
        {
            scaledWidth = (int)Math.Round((scaleWidthPercent ?? 100) / 100.0 * frameSize.Width);
            double aspectRatio = (double)sourceHeight / sourceWidth;
            scaledHeight = (int)(scaledWidth * aspectRatio);
        }

        (int horizontalMargin, int verticalMargin) = placeWithinSourceContent
            ? SourceContentMargins(
                squarePixelFrameSize,
                frameSize,
                horizontalMarginPercent ?? 0,
                verticalMarginPercent ?? 0)
            : NormalMargins(frameSize, horizontalMarginPercent ?? 0, verticalMarginPercent ?? 0);

        Location = CalculatePosition(
            location,
            frameSize.Width,
            frameSize.Height,
            scaledWidth,
            scaledHeight,
            horizontalMargin,
            verticalMargin);

        _animatedDurationSeconds = 0;

        var info = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        var scaledInfo = info.WithSize(scaledWidth, scaledHeight);

        SKCodecFrameInfo[] frameInfo = codec.FrameInfo;

        // read after FrameInfo; gif reports infinite before that
        // skia 0 means play once, but 0 means forever here
        if (codec.EncodedFormat == SKEncodedImageFormat.Gif)
        {
            _repeatCount = codec.RepetitionCount < 0 ? (ushort)0 : (ushort)Math.Max(codec.RepetitionCount, 1);
        }

        if (frameInfo.Length == 0 && codec.EncodedFormat == SKEncodedImageFormat.Png)
        {
            List<ApngFrame> apngFrames = ApngDecoder.Decode(imageBytes, info);
            foreach (ApngFrame apngFrame in apngFrames)
            {
                using (apngFrame.Bitmap)
                {
                    _scaledFrames.Add(Scale(apngFrame.Bitmap, scaledInfo));
                }

                _animatedDurationSeconds += apngFrame.DelaySeconds;
                _frameDelays.Add(apngFrame.DelaySeconds);
            }

            if (apngFrames.Count > 0)
            {
                return;
            }
        }

        if (frameInfo.Length == 0)
        {
            using SKBitmap frame = DecodeFrame(codec, info, 0, -1, []);
            _scaledFrames.Add(Scale(frame, scaledInfo));
            _frameDelays.Add(1.0 / 60.0);
            _animatedDurationSeconds = _frameDelays[0];
            return;
        }

        // later frames draw on top of earlier frames, so scale after decode
        var decodedFrames = new SKBitmap[frameInfo.Length];
        try
        {
            for (var i = 0; i < frameInfo.Length; i++)
            {
                decodedFrames[i] = DecodeFrame(codec, info, i, frameInfo[i].RequiredFrame, decodedFrames);

                _scaledFrames.Add(Scale(decodedFrames[i], scaledInfo));

                double frameDelay = frameInfo[i].Duration / 1000.0;
                _animatedDurationSeconds += frameDelay;
                _frameDelays.Add(frameDelay);
            }
        }
        finally
        {
            foreach (SKBitmap frame in decodedFrames)
            {
                frame?.Dispose();
            }
        }
    }

    private static SKBitmap DecodeFrame(
        SKCodec codec,
        SKImageInfo info,
        int frameIndex,
        int requiredFrame,
        SKBitmap[] decodedFrames)
    {
        var bitmap = new SKBitmap(info);
        var options = new SKCodecOptions(frameIndex);
        if (requiredFrame >= 0)
        {
            // codec draws only changed pixels
            decodedFrames[requiredFrame].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
            options = new SKCodecOptions(frameIndex, requiredFrame);
        }

        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), options);
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidOperationException($"Failed to decode image frame {frameIndex}: {result}");
        }

        return bitmap;
    }

    private static SKBitmap Scale(SKBitmap source, SKImageInfo scaledInfo)
    {
        if (source.Width == scaledInfo.Width && source.Height == scaledInfo.Height)
        {
            return source.Copy();
        }

        // cubic aliases when shrinking
        SKSamplingOptions sampling = scaledInfo.Width < source.Width
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

        return source.Resize(scaledInfo, sampling)
               ?? throw new InvalidOperationException("Failed to scale image.");
    }

    protected SKBitmap GetFrameForTimestamp(TimeSpan timestamp)
    {
        if (_scaledFrames.Count <= 1)
        {
            return _scaledFrames[0];
        }

        if (_repeatCount > 0 && timestamp.TotalSeconds >= _animatedDurationSeconds * _repeatCount)
        {
            return _scaledFrames.Last();
        }

        double currentTime = timestamp.TotalSeconds % _animatedDurationSeconds;

        double frameTime = 0;
        for (var i = 0; i < _scaledFrames.Count; i++)
        {
            frameTime += _frameDelays[i];
            if (currentTime <= frameTime)
            {
                return _scaledFrames[i];
            }
        }

        return _scaledFrames.Last();
    }
}
