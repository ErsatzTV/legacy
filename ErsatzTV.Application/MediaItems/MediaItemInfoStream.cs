using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.MediaItems;

public record MediaItemInfoStream(
    int? Index,
    MediaStreamKind Kind,
    string Title,
    string Codec,
    string Profile,
    string Language,
    int? Channels,
    bool? Default,
    bool? Forced,
    bool? AttachedPic,
    string PixelFormat,
    string ColorRange,
    string ColorSpace,
    string ColorTransfer,
    string ColorPrimaries,
    int? DvProfile,
    bool? HasHdr10Metadata,
    int? BitsPerRawSample,
    string MimeType)
{
    public string FileName { get; set; }
    public bool? IsExtracted { get; set; }
}
