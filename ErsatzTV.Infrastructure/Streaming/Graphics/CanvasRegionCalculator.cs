using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Graphics;
using ErsatzTV.FFmpeg.State;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public static class CanvasRegionCalculator
{
    public static Option<CanvasRegion> ForWatermarks(
        IReadOnlyCollection<WatermarkOptions> watermarks,
        FFmpegProfile profile,
        MediaVersion videoVersion)
    {
        if (watermarks.Count == 0)
        {
            return None;
        }

        // scale-and-pad placement needs the content size
        if (profile.ScalingBehavior is ScalingBehavior.ScaleAndPad &&
            (videoVersion is null || videoVersion.Width <= 0 || videoVersion.Height <= 0))
        {
            return None;
        }

        Resolution frameSize = profile.Resolution;
        Resolution squarePixelFrameSize = GraphicsEngineContextFactory.SquarePixelFrameSize(profile, videoVersion);

        SKRectI union = SKRectI.Empty;
        foreach (WatermarkOptions watermarkOptions in watermarks)
        {
            Option<SKSizeI> maybeSize = ImageSize(watermarkOptions.ImagePath);
            if (maybeSize.IsNone)
            {
                return None;
            }

            SKSizeI size = maybeSize.IfNone(SKSizeI.Empty);
            ChannelWatermark watermark = watermarkOptions.Watermark;
            SKRectI bounds = ImageElementBase.CalculateBounds(
                squarePixelFrameSize,
                frameSize,
                size.Width,
                size.Height,
                watermark.Location,
                watermark.Size == WatermarkSize.Scaled,
                watermark.WidthPercent,
                watermark.HorizontalMarginPercent,
                watermark.VerticalMarginPercent,
                watermark.PlaceWithinSourceContent);

            union = union.IsEmpty ? bounds : SKRectI.Union(union, bounds);
        }

        // chroma subsampling and hw surfaces need even edges
        int left = Math.Max(0, union.Left & ~1);
        int top = Math.Max(0, union.Top & ~1);
        int right = Math.Min(frameSize.Width, (union.Right + 1) & ~1);
        int bottom = Math.Min(frameSize.Height, (union.Bottom + 1) & ~1);

        if (right <= left || bottom <= top ||
            (left == 0 && top == 0 && right == frameSize.Width && bottom == frameSize.Height))
        {
            return None;
        }

        return new CanvasRegion(left, top, right - left, bottom - top);
    }

    private static Option<SKSizeI> ImageSize(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return None;
        }

        try
        {
            using SKCodec codec = SKCodec.Create(imagePath);
            return codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                ? None
                : new SKSizeI(codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            return None;
        }
    }
}
