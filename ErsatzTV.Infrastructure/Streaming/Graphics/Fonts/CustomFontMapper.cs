using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Topten.RichTextKit;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public sealed class CustomFontMapper(ILogger<CustomFontMapper> logger) : FontMapper
{
    private readonly ConcurrentDictionary<string, List<SKTypeface>> _customFonts = new();

    public void LoadPrivateFont(Stream stream, string familyName)
    {
        SKTypeface typeface = SKTypeface.FromStream(stream) ??
                              throw new ArgumentException("Cannot load font from stream", nameof(stream));
        string qualifiedName = familyName ?? typeface.FamilyName;

        if (typeface.FontSlant != SKFontStyleSlant.Upright)
        {
            qualifiedName += "-Italic";
        }

        _customFonts.AddOrUpdate(qualifiedName, _ => [typeface], (_, existing) => [.. existing, typeface]);
    }

    public override SKTypeface TypefaceFromStyle(IStyle style, bool ignoreFontVariants)
    {
        string qualifiedName = style.FontFamily;
        if (style.FontItalic)
        {
            qualifiedName += "-Italic";
        }

        if (_customFonts.TryGetValue(qualifiedName, out List<SKTypeface> listFonts) && listFonts.Count != 0)
        {
            return listFonts.MinBy(font => Math.Abs(font.FontWeight - style.FontWeight))!;
        }

        logger.LogWarning("Could not find font {Name}; using default", qualifiedName);

        return base.TypefaceFromStyle(style, ignoreFontVariants);
    }
}
