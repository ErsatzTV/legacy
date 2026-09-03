using System.Globalization;
using ErsatzTV.Application.Artworks;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Television;

internal static class Mapper
{
    internal static TelevisionShowViewModel ProjectToViewModel(
        Show show,
        List<string> languages,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby)
    {
        MediaSourceKind mediaSourceKind = show.LibraryPath.Library switch
        {
            PlexLibrary => MediaSourceKind.Plex,
            JellyfinLibrary => MediaSourceKind.Jellyfin,
            EmbyLibrary => MediaSourceKind.Emby,
            _ => MediaSourceKind.Local
        };

        return new TelevisionShowViewModel(
            show.Id,
            show.LibraryPath.LibraryId,
            mediaSourceKind,
            show.ShowMetadata.HeadOrNone().Map(m => m.Title ?? string.Empty).IfNone(string.Empty),
            show.ShowMetadata.HeadOrNone().Map(m => m.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .IfNone(string.Empty),
            show.ShowMetadata.HeadOrNone().Map(m => m.Plot ?? string.Empty).IfNone(string.Empty),
            show.ShowMetadata.HeadOrNone().Map(m => GetPoster(m, maybeJellyfin, maybeEmby)).IfNone(string.Empty),
            show.ShowMetadata.HeadOrNone().Map(m => GetFanArt(m, maybeJellyfin, maybeEmby)).IfNone(string.Empty),
            show.ShowMetadata.HeadOrNone().Map(m => m.Genres.Map(g => g.Name).ToList()).IfNone([]),
            show.ShowMetadata.HeadOrNone().Map(m =>
                m.Tags.Where(t => string.IsNullOrWhiteSpace(t.ExternalTypeId)).Map(g => g.Name).ToList()).IfNone([]),
            show.ShowMetadata.HeadOrNone().Map(m => m.Studios.Map(s => s.Name).ToList()).IfNone([]),
            show.ShowMetadata.HeadOrNone().Map(m =>
                m.Tags.Where(t => t.ExternalTypeId == Tag.PlexNetworkTypeId).Map(g => g.Name).ToList()).IfNone([]),
            show.ShowMetadata.HeadOrNone()
                .Map(m => (m.ContentRating ?? string.Empty).Split("/").Map(s => s.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList()).IfNone([]),
            LanguagesForShow(languages),
            show.ShowMetadata.HeadOrNone()
                .Map(m => m.Actors.OrderBy(a => a.Order).ThenBy(a => a.Id)
                    .Map(a => MediaCards.Mapper.ProjectToViewModel(a, maybeJellyfin, maybeEmby))
                    .ToList())
                .IfNone([]));
    }

    internal static TelevisionSeasonViewModel ProjectToViewModel(
        Season season,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby) =>
        new(
            season.Id,
            season.ShowId,
            season.Show.ShowMetadata.HeadOrNone().Map(m => m.Title ?? string.Empty).IfNone(string.Empty),
            season.Show.ShowMetadata.HeadOrNone()
                .Map(m => m.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).IfNone(string.Empty),
            season.SeasonNumber == 0 ? "Specials" : $"Season {season.SeasonNumber}",
            season.SeasonMetadata.HeadOrNone().Map(m => GetPoster(m, maybeJellyfin, maybeEmby))
                .IfNone(string.Empty),
            season.Show.ShowMetadata.HeadOrNone().Map(m => GetFanArt(m, maybeJellyfin, maybeEmby))
                .IfNone(string.Empty));

    private static string GetPoster(
        Metadata metadata,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby) =>
        ArtworkMapper.Artwork(metadata, ArtworkKind.Poster, maybeJellyfin, maybeEmby);

    private static string GetFanArt(
        Metadata metadata,
        Option<JellyfinMediaSource> maybeJellyfin,
        Option<EmbyMediaSource> maybeEmby) =>
        ArtworkMapper.Artwork(metadata, ArtworkKind.FanArt, maybeJellyfin, maybeEmby);

    private static List<CultureInfo> LanguagesForShow(List<string> languages)
    {
        CultureInfo[] allCultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures);

        return languages
            .Map(lang => allCultures.Filter(ci => string.Equals(
                ci.ThreeLetterISOLanguageName,
                lang,
                StringComparison.OrdinalIgnoreCase)))
            .Flatten()
            .Distinct()
            .ToList();
    }
}
