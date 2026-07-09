using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Images;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ErsatzTV.Core.Images;

public class ChannelLogoGenerator : IChannelLogoGenerator
{
    public const string GetRoute = "/iptv/logos/gen";
    public const string GetRouteQueryParamName = "text";

    private readonly ILogger _logger;

    public ChannelLogoGenerator(
        ILogger<ChannelLogoGenerator> logger) =>
        _logger = logger;

    public Either<BaseError, byte[]> GenerateChannelLogo(
        string text,
        int logoHeight,
        int logoWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(logoWidth, logoHeight));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            //etv logo
            string overlayImagePath = Path.Combine("wwwroot", "images", "ersatztv-500.png");
            using var overlayImage = SKBitmap.Decode(overlayImagePath);
            canvas.DrawBitmap(overlayImage, new SKRect(155, 60, 205, 110), SKSamplingOptions.Default);

            //Custom Font
            string fontPath = Path.Combine(FileSystemLayout.ResourcesCacheFolder, "Sen.ttf");
            using var fontTypeface = SKTypeface.FromFile(fontPath);
            var fontSize = 30;
            var font = new SKFont
            {
                Typeface = fontTypeface,
                Size = fontSize
            };

            var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                Style = SKPaintStyle.Fill
            };

            font.MeasureText(text, out SKRect textBounds, paint);

            // Ajuster la taille de la police si nécessaire
            while (textBounds.Width > logoWidth - 10 && fontSize > 16)
            {
                fontSize -= 2;
                font.Size = fontSize;
                font.MeasureText(text, out textBounds, paint);
            }

            // Dessiner le texte
            float x = logoWidth / 2f;
            float y = logoHeight / 2f - textBounds.MidY;
            canvas.DrawText(text, x, y, SKTextAlign.Center, font, paint);

            using SKImage image = surface.Snapshot();
            using var ms = new MemoryStream();
            image.Encode(SKEncodedImageFormat.Png, 100).SaveTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError("Can't generate Channel Logo ([{ErrorType}] {ErrorMessage})", ex.GetType(), ex.Message);
            return BaseError.New("Can't generate Channel Logo " + ex.Message);
        }
    }

    public static string GenerateChannelLogoUrl(Channel channel) =>
        $"http://localhost:{Settings.StreamingPort}{GetRoute}?{GetRouteQueryParamName}={channel.WebEncodedName}";
}
