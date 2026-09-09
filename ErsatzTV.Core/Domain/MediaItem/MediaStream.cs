using System.Diagnostics.CodeAnalysis;

namespace ErsatzTV.Core.Domain;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public class MediaStream
{
    public int Id { get; set; }
    public int Index { get; set; }
    public string Codec { get; set; }
    public string Profile { get; set; }
    public MediaStreamKind MediaStreamKind { get; set; }
    public string Language { get; set; }
    public int Channels { get; set; }
    public string Title { get; set; }
    public bool Default { get; set; }
    public bool Forced { get; set; }
    public bool AttachedPic { get; set; }
    public string PixelFormat { get; set; }
    public string ColorRange { get; set; }
    public string ColorSpace { get; set; }
    public string ColorTransfer { get; set; }
    public string ColorPrimaries { get; set; }
    public int? DvProfile { get; set; }
    public bool HasHdr10Metadata { get; set; }
    public int BitsPerRawSample { get; set; }
    public string FileName { get; set; }
    public string MimeType { get; set; }
    public int MediaVersionId { get; set; }
    public MediaVersion MediaVersion { get; set; }
}
