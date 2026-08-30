using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Plex;

internal static class Mapper
{
    internal static PlexMediaSourceViewModel ProjectToViewModel(PlexMediaSource plexMediaSource) =>
        new(
            plexMediaSource.Id,
            plexMediaSource.ServerName,
            Optional(plexMediaSource.Connections.SingleOrDefault(c => c.IsActive)).Match(c => c.Uri, string.Empty),
            plexMediaSource.MissingSince);

    internal static PlexLibraryViewModel ProjectToViewModel(PlexLibrary library) =>
        new(library.Id, library.Name, library.MediaKind, library.ShouldSyncItems);

    internal static PlexPathReplacementViewModel ProjectToViewModel(PlexPathReplacement pathReplacement) =>
        new(pathReplacement.Id, pathReplacement.PlexPath, pathReplacement.LocalPath);
}
