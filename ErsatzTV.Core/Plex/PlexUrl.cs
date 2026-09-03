using System.Globalization;
using ErsatzTV.Core.Domain;
using Flurl;

namespace ErsatzTV.Core.Plex;

public static class PlexUrl
{
    public static string PlaceholderProxyForArtwork(int artworkId, ArtworkKind artworkKind)
    {
        string artworkFolder = artworkKind switch
        {
            ArtworkKind.Thumbnail => "thumbnails",
            _ => "posters"
        };

        return Url.Parse($"http://not-a-real-host/iptv/artwork/{artworkFolder}/plex")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture))
            .ToString()
            .Replace("http://not-a-real-host", "{RequestBase}");
    }

    public static Url RelativeProxyForArtwork(int artworkId) =>
        Url.Parse("plex")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture));
}
