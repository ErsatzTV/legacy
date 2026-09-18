using Microsoft.Extensions.Logging;
using Topten.RichTextKit;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class GraphicsEngineFonts(CustomFontMapper mapper, ILogger<GraphicsEngineFonts> logger)
{
    private readonly Lock _sync = new();

    private readonly System.Collections.Generic.HashSet<string>
        _loadedFontFiles = new(StringComparer.OrdinalIgnoreCase);

    public FontMapper Mapper => mapper;

    public void LoadFonts(string fontsFolder)
    {
        lock (_sync)
        {
            foreach (string file in Directory.EnumerateFiles(fontsFolder, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_loadedFontFiles.Contains(file))
                {
                    continue;
                }

                try
                {
                    using FileStream stream = File.OpenRead(file);
                    mapper.LoadPrivateFont(stream, null);
                    _loadedFontFiles.Add(file);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load font file: {File}", file);
                }
            }
        }
    }
}
