using System.Globalization;
using ErsatzTV.Core.Domain;
using Flurl;

namespace ErsatzTV.Core.Jellyfin;

public static class JellyfinUrl
{
    public static Url ForArtwork(string address, string artwork)
    {
        string[] split = artwork.Replace("jellyfin://", string.Empty).Split('?');
        if (split.Length != 2)
        {
            return artwork;
        }

        string pathSegment = split[0];
        QueryParamCollection query = Url.ParseQueryParams(split[1]);

        return Url.Parse(address)
            .AppendPathSegment(pathSegment)
            .SetQueryParams(query);
    }

    public static string PlaceholderProxyForArtwork(int artworkId, ArtworkKind artworkKind)
    {
        string artworkFolder = artworkKind switch
        {
            ArtworkKind.Thumbnail => "thumbnails",
            _ => "posters"
        };

        return Url.Parse($"http://not-a-real-host/iptv/artwork/{artworkFolder}/jellyfin")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture))
            .ToString()
            .Replace("http://not-a-real-host", "{RequestBase}");
    }

    public static Url RelativeProxyForArtwork(int artworkId) =>
        Url.Parse("jellyfin")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture));
}
