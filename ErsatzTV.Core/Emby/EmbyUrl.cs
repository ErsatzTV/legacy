using System.Globalization;
using ErsatzTV.Core.Domain;
using Flurl;

namespace ErsatzTV.Core.Emby;

public static class EmbyUrl
{
    public static Url ForArtwork(string address, string artwork)
    {
        string[] split = artwork.Replace("emby://", string.Empty).Split('?');
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

        return Url.Parse($"http://not-a-real-host/iptv/artwork/{artworkFolder}/emby")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture))
            .ToString()
            .Replace("http://not-a-real-host", "{RequestBase}");
    }

    public static Url RelativeProxyForArtwork(int artworkId) =>
        Url.Parse("emby")
            .AppendPathSegment(artworkId.ToString(CultureInfo.InvariantCulture));
}
