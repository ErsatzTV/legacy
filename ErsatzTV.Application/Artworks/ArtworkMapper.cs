using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Plex;
using Flurl;

namespace ErsatzTV.Application.Artworks;

public static class ArtworkMapper
{
    public static string Artwork(
        Metadata metadata,
        ArtworkKind artworkKind,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby)
    {
        foreach (var artwork in Optional(metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == artworkKind)))
        {
            return Artwork(artwork, artworkKind, maybeJellyfin, maybeEmby);
        }

        return string.Empty;
    }

    public static string Artwork(
        Artwork artwork,
        ArtworkKind artworkKind,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby)
    {
        string artworkPath = artwork.Path ?? string.Empty;

        if (artworkPath.StartsWith("plex/", StringComparison.OrdinalIgnoreCase))
        {
            Url url = PlexUrl.RelativeProxyForArtwork(artwork.Id);
            artworkPath = url;
        }
        else if (maybeJellyfin.IsSome && artworkPath.StartsWith("jellyfin://", StringComparison.OrdinalIgnoreCase))
        {

            Url url = JellyfinUrl.RelativeProxyForArtwork(artwork.Id);
            artworkPath = url;
        }
        else if (maybeEmby.IsSome && artworkPath.StartsWith("emby://", StringComparison.OrdinalIgnoreCase))
        {
            Url url = EmbyUrl.RelativeProxyForArtwork(artwork.Id);
            artworkPath = url;
        }

        return artworkPath;
    }
}
