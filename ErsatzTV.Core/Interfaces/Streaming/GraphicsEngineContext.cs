using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Graphics;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Core.Interfaces.Streaming;

public record GraphicsEngineContext(
    string ChannelNumber,
    MediaItem MediaItem,
    List<GraphicsElementContext> Elements,
    Dictionary<string, object> TemplateVariables,
    Resolution SquarePixelFrameSize,
    Resolution FrameSize,
    FrameRate FrameRate,
    DateTimeOffset ChannelStartTime,
    DateTimeOffset ContentStartTime,
    TimeSpan Seek,
    TimeSpan Duration,
    TimeSpan ContentTotalDuration);

public abstract record GraphicsElementContext;

public record WatermarkElementContext(WatermarkOptions Options) : GraphicsElementContext;

public record TextElementDataContext(TextGraphicsElement TextElement) : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => TextElement.EpgEntries;
}

public record ImageElementDataContext(ImageGraphicsElement ImageElement) : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => ImageElement.EpgEntries;
}

public record MotionElementDataContext(MotionGraphicsElement MotionElement)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => MotionElement.EpgEntries;
}

public record SubtitleElementDataContext(
    SubtitleGraphicsElement SubtitleElement,
    Dictionary<string, string> Variables)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => SubtitleElement.EpgEntries;
}

public record ScriptElementDataContext(ScriptGraphicsElement ScriptElement)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => ScriptElement.EpgEntries;
}


public interface ITemplateDataContext
{
    int EpgEntries { get; }
}
