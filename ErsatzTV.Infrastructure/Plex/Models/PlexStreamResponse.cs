using System.Globalization;
using System.Xml.Serialization;

namespace ErsatzTV.Infrastructure.Plex.Models;

public class PlexStreamResponse
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    [XmlIgnore]
    public int? Index { get; set; }

    [XmlAttribute("index")]
    public string IndexString
    {
        get => Index.HasValue ? Index.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => Index = !string.IsNullOrEmpty(value) ? int.Parse(value, CultureInfo.InvariantCulture) : null;
    }

    [XmlAttribute("key")]
    public string Key { get; set; }

    [XmlAttribute("default")]
    public bool Default { get; set; }

    [XmlAttribute("forced")]
    public bool Forced { get; set; }

    [XmlAttribute("embeddedInVideo")]
    public bool EmbeddedInVideo { get; set; }

    [XmlAttribute("languageCode")]
    public string LanguageCode { get; set; }

    [XmlAttribute("title")]
    public string Title { get; set; }

    [XmlAttribute("streamType")]
    public int StreamType { get; set; }

    [XmlAttribute("codec")]
    public string Codec { get; set; }

    [XmlAttribute("profile")]
    public string Profile { get; set; }

    [XmlAttribute("channels")]
    public int Channels { get; set; }

    [XmlAttribute("width")]
    public int Width { get; set; }

    [XmlAttribute("height")]
    public int Height { get; set; }

    [XmlAttribute("anamorphic")]
    public bool Anamorphic { get; set; }

    [XmlAttribute("pixelAspectRatio")]
    public string PixelAspectRatio { get; set; }

    [XmlAttribute("scanType")]
    public string ScanType { get; set; }

    [XmlAttribute("frameRate")]
    public string FrameRate { get; set; }

    [XmlAttribute("bitDepth")]
    public int BitDepth { get; set; }

    [XmlAttribute("colorRange")]
    public string ColorRange { get; set; }

    [XmlAttribute("colorSpace")]
    public string ColorSpace { get; set; }

    [XmlAttribute("colorTrc")]
    public string ColorTrc { get; set; }

    [XmlAttribute("colorPrimaries")]
    public string ColorPrimaries { get; set; }

    [XmlAttribute("displayTitle")]
    public string DisplayTitle { get; set; }

    [XmlAttribute("extendedDisplayTitle")]
    public string ExtendedDisplayTitle { get; set; }

    [XmlIgnore]
    public int? Orientation { get; set; }

    [XmlAttribute("orientation")]
    public string OrientationString
    {
        get => Orientation.HasValue ? Orientation.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => Orientation = !string.IsNullOrEmpty(value) ? int.Parse(value, CultureInfo.InvariantCulture) : null;
    }
}
