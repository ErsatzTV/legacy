using ErsatzTV.Application.MediaSources;

namespace ErsatzTV.Application.Plex;

public record PlexMediaSourceViewModel(int Id, string Name, string Address, DateTime? MissingSince)
    : MediaSourceViewModel(Id, Name);
