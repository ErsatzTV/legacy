using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Infrastructure.Metadata;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using ErsatzTV.Scanner.Core.Interfaces.Metadata.Nfo;
using ErsatzTV.Scanner.Core.Metadata.Nfo;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public class LocalMetadataProvider : ILocalMetadataProvider
{
    private readonly IArtistNfoReader _artistNfoReader;
    private readonly IArtistRepository _artistRepository;
    private readonly IEpisodeNfoReader _episodeNfoReader;
    private readonly IFallbackMetadataProvider _fallbackMetadataProvider;
    private readonly IFileSystem _fileSystem;
    private readonly IImageRepository _imageRepository;
    private readonly ILocalStatisticsProvider _localStatisticsProvider;
    private readonly ILogger<LocalMetadataProvider> _logger;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IMovieNfoReader _movieNfoReader;
    private readonly IMovieRepository _movieRepository;
    private readonly IMusicVideoNfoReader _musicVideoNfoReader;
    private readonly IMusicVideoRepository _musicVideoRepository;
    private readonly IOtherVideoNfoReader _otherVideoNfoReader;
    private readonly IOtherVideoRepository _otherVideoRepository;
    private readonly IRemoteStreamRepository _remoteStreamRepository;
    private readonly IShowNfoReader _showNfoReader;
    private readonly ISongRepository _songRepository;
    private readonly ITelevisionRepository _televisionRepository;

    public LocalMetadataProvider(
        IMetadataRepository metadataRepository,
        IMovieRepository movieRepository,
        ITelevisionRepository televisionRepository,
        IArtistRepository artistRepository,
        IMusicVideoRepository musicVideoRepository,
        IOtherVideoRepository otherVideoRepository,
        ISongRepository songRepository,
        IImageRepository imageRepository,
        IRemoteStreamRepository remoteStreamRepository,
        IFallbackMetadataProvider fallbackMetadataProvider,
        IFileSystem fileSystem,
        IMovieNfoReader movieNfoReader,
        IEpisodeNfoReader episodeNfoReader,
        IArtistNfoReader artistNfoReader,
        IMusicVideoNfoReader musicVideoNfoReader,
        IShowNfoReader showNfoReader,
        IOtherVideoNfoReader otherVideoNfoReader,
        ILocalStatisticsProvider localStatisticsProvider,
        ILogger<LocalMetadataProvider> logger)
    {
        _metadataRepository = metadataRepository;
        _movieRepository = movieRepository;
        _televisionRepository = televisionRepository;
        _artistRepository = artistRepository;
        _musicVideoRepository = musicVideoRepository;
        _otherVideoRepository = otherVideoRepository;
        _songRepository = songRepository;
        _imageRepository = imageRepository;
        _remoteStreamRepository = remoteStreamRepository;
        _fallbackMetadataProvider = fallbackMetadataProvider;
        _fileSystem = fileSystem;
        _movieNfoReader = movieNfoReader;
        _episodeNfoReader = episodeNfoReader;
        _artistNfoReader = artistNfoReader;
        _musicVideoNfoReader = musicVideoNfoReader;
        _showNfoReader = showNfoReader;
        _otherVideoNfoReader = otherVideoNfoReader;
        _localStatisticsProvider = localStatisticsProvider;
        _logger = logger;
    }

    public async Task<ShowMetadata> GetMetadataForShow(string showFolder)
    {
        string nfoFileName = Path.Combine(showFolder, "tvshow.nfo");
        Option<ShowMetadata> maybeMetadata = None;
        if (_fileSystem.File.Exists(nfoFileName))
        {
            maybeMetadata = await LoadTelevisionShowMetadata(nfoFileName);
        }

        foreach (ShowMetadata metadata in maybeMetadata)
        {
            metadata.SortTitle = SortTitle.GetSortTitle(metadata.Title);
            return metadata;
        }

        ShowMetadata fallbackMetadata = _fallbackMetadataProvider.GetFallbackMetadataForShow(showFolder);
        fallbackMetadata.SortTitle = SortTitle.GetSortTitle(fallbackMetadata.Title);
        return fallbackMetadata;
    }

    public async Task<ArtistMetadata> GetMetadataForArtist(string artistFolder)
    {
        string nfoFileName = Path.Combine(artistFolder, "artist.nfo");
        Option<ArtistMetadata> maybeMetadata = None;
        if (_fileSystem.File.Exists(nfoFileName))
        {
            maybeMetadata = await LoadArtistMetadata(nfoFileName);
        }

        foreach (ArtistMetadata metadata in maybeMetadata)
        {
            metadata.SortTitle = SortTitle.GetSortTitle(metadata.Title);
            return metadata;
        }

        ArtistMetadata fallbackMetadata = _fallbackMetadataProvider.GetFallbackMetadataForArtist(artistFolder);
        fallbackMetadata.SortTitle = SortTitle.GetSortTitle(fallbackMetadata.Title);
        return fallbackMetadata;
    }

    public async Task<bool> RefreshSidecarMetadata(Movie movie, string nfoFileName)
    {
        Option<MovieMetadata> maybeMetadata = await LoadMovieMetadata(movie, nfoFileName);
        foreach (MovieMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(movie, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshSidecarMetadata(Show televisionShow, string nfoFileName)
    {
        Option<ShowMetadata> maybeMetadata = await LoadTelevisionShowMetadata(nfoFileName);
        foreach (ShowMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(televisionShow, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshSidecarMetadata(Episode episode, string nfoFileName)
    {
        List<EpisodeMetadata> metadata = await LoadEpisodeMetadata(episode, nfoFileName);
        return await ApplyMetadataUpdate(episode, metadata);
    }

    public async Task<bool> RefreshSidecarMetadata(Artist artist, string nfoFileName)
    {
        Option<ArtistMetadata> maybeMetadata = await LoadArtistMetadata(nfoFileName);
        foreach (ArtistMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(artist, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshSidecarMetadata(MusicVideo musicVideo, string nfoFileName)
    {
        Option<MusicVideoMetadata> maybeMetadata = await LoadMusicVideoMetadata(nfoFileName);
        foreach (MusicVideoMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(musicVideo, metadata);
        }

        return await RefreshFallbackMetadata(musicVideo);
    }

    public async Task<bool> RefreshSidecarMetadata(OtherVideo otherVideo, string nfoFileName)
    {
        Option<OtherVideoMetadata> maybeMetadata = await LoadOtherVideoMetadata(nfoFileName);
        foreach (OtherVideoMetadata metadata in maybeMetadata)
        {
            // merge path-based tags with nfo tags
            string? folder = Path.GetDirectoryName(nfoFileName);
            if (folder != null)
            {
                string libraryPath = otherVideo.LibraryPath.Path;
                string parent = Optional(Directory.GetParent(libraryPath)).Match(
                    di => di.FullName,
                    () => libraryPath);

                string diff = Path.GetRelativePath(parent, folder);

                var tags = diff.Split(Path.DirectorySeparatorChar)
                    .Filter(t => metadata.Tags.All(mt => mt.Name != t))
                    .Map(t => new Tag { Name = t })
                    .ToList();

                metadata.Tags.AddRange(tags);
            }

            return await ApplyMetadataUpdate(otherVideo, metadata);
        }

        return await RefreshFallbackMetadata(otherVideo);
    }

    public async Task<bool> RefreshTagMetadata(Song song)
    {
        Option<SongMetadata> maybeMetadata = LoadSongMetadata(song);
        foreach (SongMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(song, metadata);
        }

        return await RefreshFallbackMetadata(song);
    }

    public async Task<bool> RefreshTagMetadata(Image image, double? durationSeconds)
    {
        Option<ImageMetadata> maybeMetadata = LoadImageMetadata(image, durationSeconds);
        foreach (ImageMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(image, metadata);
        }

        return await RefreshFallbackMetadata(image);
    }

    public async Task<bool> RefreshMetadata(
        RemoteStream remoteStream,
        YamlRemoteStreamDefinition definition,
        CancellationToken cancellationToken)
    {
        Option<RemoteStreamMetadata> maybeMetadata = LoadRemoteStreamMetadata(remoteStream, definition);
        foreach (RemoteStreamMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(remoteStream, metadata, cancellationToken);
        }

        return await RefreshFallbackMetadata(remoteStream, cancellationToken);
    }

    public Task<bool> RefreshFallbackMetadata(Movie movie) =>
        ApplyMetadataUpdate(movie, _fallbackMetadataProvider.GetFallbackMetadata(movie));

    public Task<bool> RefreshFallbackMetadata(Episode episode) =>
        ApplyMetadataUpdate(episode, _fallbackMetadataProvider.GetFallbackMetadata(episode));

    public Task<bool> RefreshFallbackMetadata(Artist artist, string artistFolder) =>
        ApplyMetadataUpdate(artist, _fallbackMetadataProvider.GetFallbackMetadataForArtist(artistFolder));

    public async Task<bool> RefreshFallbackMetadata(OtherVideo otherVideo)
    {
        Option<OtherVideoMetadata> maybeMetadata = _fallbackMetadataProvider.GetFallbackMetadata(otherVideo);
        foreach (OtherVideoMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(otherVideo, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshFallbackMetadata(Song song)
    {
        Option<SongMetadata> maybeMetadata = _fallbackMetadataProvider.GetFallbackMetadata(song);
        foreach (SongMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(song, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshFallbackMetadata(Image image)
    {
        Option<ImageMetadata> maybeMetadata = _fallbackMetadataProvider.GetFallbackMetadata(image);
        foreach (ImageMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(image, metadata);
        }

        return false;
    }

    public async Task<bool> RefreshFallbackMetadata(RemoteStream remoteStream, CancellationToken cancellationToken)
    {
        Option<RemoteStreamMetadata> maybeMetadata = _fallbackMetadataProvider.GetFallbackMetadata(remoteStream);
        foreach (RemoteStreamMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(remoteStream, metadata, cancellationToken);
        }

        return false;
    }

    public async Task<bool> RefreshFallbackMetadata(MusicVideo musicVideo)
    {
        Option<MusicVideoMetadata> maybeMetadata = _fallbackMetadataProvider.GetFallbackMetadata(musicVideo);
        foreach (MusicVideoMetadata metadata in maybeMetadata)
        {
            return await ApplyMetadataUpdate(musicVideo, metadata);
        }

        return false;
    }

    public Task<bool> RefreshFallbackMetadata(Show televisionShow, string showFolder) =>
        ApplyMetadataUpdate(televisionShow, _fallbackMetadataProvider.GetFallbackMetadataForShow(showFolder));

    private async Task<Option<MusicVideoMetadata>> LoadMusicVideoMetadata(string nfoFileName)
    {
        try
        {
            Either<BaseError, MusicVideoNfo> maybeNfo = await _musicVideoNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read MusicVideo nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());
            }

            foreach (MusicVideoNfo nfo in maybeNfo.RightToSeq())
            {
                return new MusicVideoMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName),
                    Album = nfo.Album,
                    Title = nfo.Title,
                    Plot = nfo.Plot,
                    Track = nfo.Track,
                    Year = GetYear(nfo.Year, nfo.Aired),
                    ReleaseDate = GetAired(nfo.Year, nfo.Aired),
                    Artists = nfo.Artists.Map(a => new MusicVideoArtist { Name = a }).ToList(),
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Tags = nfo.Tags.Map(t => new Tag { Name = t }).ToList(),
                    Studios = nfo.Studios.Map(s => new Studio { Name = s }).ToList(),
                    Directors = nfo.Directors.Map(s => new Director { Name = s }).ToList(),
                    Artwork = []
                };
            }

            return None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read music video nfo metadata from {Path}", nfoFileName);
            return None;
        }
    }

    private Option<SongMetadata> LoadSongMetadata(Song song)
    {
        string path = song.GetHeadVersion().MediaFiles.Head().Path;

        try
        {
            Either<BaseError, List<SongTag>> maybeTags = _localStatisticsProvider.GetSongTags(song);

            foreach (List<SongTag> tags in maybeTags.RightToSeq())
            {
                Option<SongMetadata> maybeFallbackMetadata = _fallbackMetadataProvider.GetFallbackMetadata(song);

                var result = new SongMetadata
                {
                    MetadataKind = MetadataKind.Embedded,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = _fileSystem.File.GetLastWriteTimeUtc(path),

                    Artists = [],
                    AlbumArtists = [],

                    Artwork = [],
                    Actors = [],
                    Genres = [],
                    Studios = [],
                    Tags = []
                };

                foreach (SongTag tag in tags)
                {
                    switch (tag.Tag)
                    {
                        case MetadataSongTag.Album:
                            result.Album = tag.Value;
                            break;
                        case MetadataSongTag.Artist:
                            result.Artists.Add(tag.Value);
                            break;
                        case MetadataSongTag.AlbumArtist:
                            result.AlbumArtists.Add(tag.Value);
                            break;
                        case MetadataSongTag.Genre:
                            result.Genres.Add(new Genre { Name = tag.Value });
                            break;
                        case MetadataSongTag.Title:
                            result.Title = tag.Value;
                            break;
                        case MetadataSongTag.Track:
                            result.Track = tag.Value;
                            break;
                        case MetadataSongTag.Comment:
                            result.Comment = tag.Value;
                            break;
                    }
                }

                foreach (SongMetadata fallbackMetadata in maybeFallbackMetadata)
                {
                    if (string.IsNullOrWhiteSpace(result.Title))
                    {
                        result.Title = fallbackMetadata.Title;
                    }

                    result.OriginalTitle = fallbackMetadata.OriginalTitle;

                    // preserve folder tagging - maybe someone uses this
                    foreach (Tag tag in fallbackMetadata.Tags)
                    {
                        result.Tags.Add(tag);
                    }
                }

                return result;
            }

            return Option<SongMetadata>.None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read embedded song metadata from {Path}", path);
            return None;
        }
    }

    private Option<ImageMetadata> LoadImageMetadata(Image image, double? durationSeconds)
    {
        string path = image.GetHeadVersion().MediaFiles.Head().Path;

        try
        {
            Either<BaseError, List<SongTag>> maybeTags = _localStatisticsProvider.GetSongTags(image);

            foreach (List<SongTag> tags in maybeTags.RightToSeq())
            {
                Option<ImageMetadata> maybeFallbackMetadata = _fallbackMetadataProvider.GetFallbackMetadata(image);

                var result = new ImageMetadata
                {
                    MetadataKind = MetadataKind.Embedded,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = _fileSystem.File.GetLastWriteTimeUtc(path),
                    DurationSeconds = durationSeconds,

                    Artwork = [],
                    Actors = [],
                    Genres = [],
                    Studios = [],
                    Tags = []
                };

                foreach (SongTag tag in tags)
                {
                    switch (tag.Tag)
                    {
                        case MetadataSongTag.Genre:
                            result.Genres.Add(new Genre { Name = tag.Value });
                            break;
                        case MetadataSongTag.Title:
                            result.Title = tag.Value;
                            break;
                    }
                }

                foreach (ImageMetadata fallbackMetadata in maybeFallbackMetadata)
                {
                    if (string.IsNullOrWhiteSpace(result.Title))
                    {
                        result.Title = fallbackMetadata.Title;
                    }

                    result.OriginalTitle = fallbackMetadata.OriginalTitle;

                    // preserve folder tagging
                    foreach (Tag tag in fallbackMetadata.Tags)
                    {
                        result.Tags.Add(tag);
                    }
                }

                return result;
            }

            return Option<ImageMetadata>.None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read embedded song metadata from {Path}", path);
            return None;
        }
    }

    private Option<RemoteStreamMetadata> LoadRemoteStreamMetadata(
        RemoteStream remoteStream,
        YamlRemoteStreamDefinition definition)
    {
        string path = remoteStream.GetHeadVersion().MediaFiles.Head().Path;

        try
        {
            Option<RemoteStreamMetadata> maybeFallbackMetadata =
                _fallbackMetadataProvider.GetFallbackMetadata(remoteStream);

            var result = new RemoteStreamMetadata
            {
                MetadataKind = MetadataKind.Embedded,
                DateAdded = DateTime.UtcNow,
                DateUpdated = _fileSystem.File.GetLastWriteTimeUtc(path),

                Artwork = [],
                Actors = [],
                Genres = [],
                Studios = [],
                Tags = [],

                Title = definition.Title,
                Plot = definition.Plot,
                ContentRating = definition.ContentRating,
                Year = definition.Year
            };

            foreach (RemoteStreamMetadata fallbackMetadata in maybeFallbackMetadata)
            {
                // title is not required in remote stream definition; use fallback title if missing
                if (string.IsNullOrWhiteSpace(result.Title))
                {
                    result.Title = fallbackMetadata.Title;
                }

                result.OriginalTitle = fallbackMetadata.OriginalTitle;

                // preserve folder tagging
                foreach (Tag tag in fallbackMetadata.Tags)
                {
                    result.Tags.Add(tag);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read embedded remote stream metadata from {Path}", path);
            return None;
        }
    }

    private async Task<bool> ApplyMetadataUpdate(Episode episode, List<EpisodeMetadata> episodeMetadata)
    {
        var updated = false;

        episode.EpisodeMetadata ??= [];

        var toUpdate = episode.EpisodeMetadata
            .Where(em => episodeMetadata.Any(em2 => em2.EpisodeNumber == em.EpisodeNumber))
            .ToList();
        var toRemove = episode.EpisodeMetadata.Except(toUpdate).ToList();
        var toAdd = episodeMetadata
            .Where(em => episode.EpisodeMetadata.All(em2 => em2.EpisodeNumber != em.EpisodeNumber))
            .ToList();

        foreach (EpisodeMetadata metadata in toRemove)
        {
            await _televisionRepository.RemoveMetadata(episode, metadata);
            updated = true;
        }

        foreach (EpisodeMetadata metadata in toAdd)
        {
            metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;
            metadata.EpisodeId = episode.Id;
            metadata.Episode = episode;
            episode.EpisodeMetadata.Add(metadata);

            updated = await _metadataRepository.Add(metadata) || updated;
        }

        foreach (EpisodeMetadata existing in toUpdate)
        {
            Option<EpisodeMetadata> maybeIncoming =
                episodeMetadata.Find(em => em.EpisodeNumber == existing.EpisodeNumber);
            foreach (EpisodeMetadata metadata in maybeIncoming)
            {
                existing.Outline = metadata.Outline;
                existing.Plot = metadata.Plot;
                existing.Tagline = metadata.Tagline;
                existing.Title = metadata.Title;

                if (existing.DateAdded == SystemTime.MinValueUtc)
                {
                    existing.DateAdded = metadata.DateAdded;
                }

                existing.DateUpdated = metadata.DateUpdated;
                existing.MetadataKind = metadata.MetadataKind;
                existing.OriginalTitle = metadata.OriginalTitle;
                existing.ReleaseDate = metadata.ReleaseDate;
                existing.Year = metadata.Year;
                existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                    ? SortTitle.GetSortTitle(metadata.Title)
                    : metadata.SortTitle;

                updated = await UpdateMetadataCollections(
                    existing,
                    metadata,
                    _televisionRepository.AddGenre,
                    _televisionRepository.AddTag,
                    (_, _) => Task.FromResult(false),
                    _televisionRepository.AddActor) || updated;

                foreach (Director director in existing.Directors
                             .Filter(d => metadata.Directors.All(d2 => d2.Name != d.Name)).ToList())
                {
                    existing.Directors.Remove(director);
                    if (await _metadataRepository.RemoveDirector(director))
                    {
                        updated = true;
                    }
                }

                foreach (Director director in metadata.Directors
                             .Filter(d => existing.Directors.All(d2 => d2.Name != d.Name)).ToList())
                {
                    existing.Directors.Add(director);
                    if (await _televisionRepository.AddDirector(existing, director))
                    {
                        updated = true;
                    }
                }

                foreach (Writer writer in existing.Writers
                             .Filter(w => metadata.Writers.All(w2 => w2.Name != w.Name)).ToList())
                {
                    existing.Writers.Remove(writer);
                    if (await _metadataRepository.RemoveWriter(writer))
                    {
                        updated = true;
                    }
                }

                foreach (Writer writer in metadata.Writers
                             .Filter(w => existing.Writers.All(w2 => w2.Name != w.Name)).ToList())
                {
                    existing.Writers.Add(writer);
                    if (await _televisionRepository.AddWriter(existing, writer))
                    {
                        updated = true;
                    }
                }

                foreach (MetadataGuid guid in existing.Guids
                             .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
                {
                    existing.Guids.Remove(guid);
                    if (await _metadataRepository.RemoveGuid(guid))
                    {
                        updated = true;
                    }
                }

                foreach (MetadataGuid guid in metadata.Guids
                             .Filter(g => existing.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
                {
                    existing.Guids.Add(guid);
                    if (await _metadataRepository.AddGuid(existing, guid))
                    {
                        updated = true;
                    }
                }

                return await _metadataRepository.Update(existing) || updated;
            }
        }

        return updated;
    }

    private async Task<bool> ApplyMetadataUpdate(Movie movie, MovieMetadata metadata)
    {
        Option<MovieMetadata> maybeMetadata = Optional(movie.MovieMetadata).Flatten().HeadOrNone();
        foreach (MovieMetadata existing in maybeMetadata)
        {
            existing.ContentRating = metadata.ContentRating;
            existing.Outline = metadata.Outline;
            existing.Plot = metadata.Plot;
            existing.Tagline = metadata.Tagline;
            existing.Title = metadata.Title;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.Year = metadata.Year;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                _movieRepository.AddGenre,
                _movieRepository.AddTag,
                _movieRepository.AddStudio,
                _movieRepository.AddActor);

            foreach (Director director in existing.Directors
                         .Filter(d => metadata.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Remove(director);
                if (await _metadataRepository.RemoveDirector(director))
                {
                    updated = true;
                }
            }

            foreach (Director director in metadata.Directors
                         .Filter(d => existing.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Add(director);
                if (await _movieRepository.AddDirector(existing, director))
                {
                    updated = true;
                }
            }

            foreach (Writer writer in existing.Writers
                         .Filter(w => metadata.Writers.All(w2 => w2.Name != w.Name)).ToList())
            {
                existing.Writers.Remove(writer);
                if (await _metadataRepository.RemoveWriter(writer))
                {
                    updated = true;
                }
            }

            foreach (Writer writer in metadata.Writers
                         .Filter(w => existing.Writers.All(w2 => w2.Name != w.Name)).ToList())
            {
                existing.Writers.Add(writer);
                if (await _movieRepository.AddWriter(existing, writer))
                {
                    updated = true;
                }
            }

            foreach (MetadataGuid guid in existing.Guids
                         .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Remove(guid);
                if (await _metadataRepository.RemoveGuid(guid))
                {
                    updated = true;
                }
            }

            foreach (MetadataGuid guid in metadata.Guids
                         .Filter(g => existing.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Add(guid);
                if (await _metadataRepository.AddGuid(existing, guid))
                {
                    updated = true;
                }
            }

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.MovieId = movie.Id;
        movie.MovieMetadata = new List<MovieMetadata> { metadata };

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(Show show, ShowMetadata metadata)
    {
        Option<ShowMetadata> maybeMetadata = Optional(show.ShowMetadata).Flatten().HeadOrNone();
        foreach (ShowMetadata existing in maybeMetadata)
        {
            existing.ContentRating = metadata.ContentRating;
            existing.Outline = metadata.Outline;
            existing.Plot = metadata.Plot;
            existing.Tagline = metadata.Tagline;
            existing.Title = metadata.Title;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.Year = metadata.Year;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                _televisionRepository.AddGenre,
                _televisionRepository.AddTag,
                _televisionRepository.AddStudio,
                _televisionRepository.AddActor);

            foreach (MetadataGuid guid in existing.Guids
                         .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Remove(guid);
                if (await _metadataRepository.RemoveGuid(guid))
                {
                    updated = true;
                }
            }

            foreach (MetadataGuid guid in metadata.Guids
                         .Filter(g => existing.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Add(guid);
                if (await _metadataRepository.AddGuid(existing, guid))
                {
                    updated = true;
                }
            }

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.ShowId = show.Id;
        show.ShowMetadata = new List<ShowMetadata> { metadata };

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(Artist artist, ArtistMetadata metadata)
    {
        Option<ArtistMetadata> maybeMetadata = Optional(artist.ArtistMetadata).Flatten().HeadOrNone();
        foreach (ArtistMetadata existing in maybeMetadata)
        {
            existing.Title = metadata.Title;
            existing.Disambiguation = metadata.Disambiguation;
            existing.Biography = metadata.Biography;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;

            var updated = false;

            foreach (Genre genre in existing.Genres.Filter(g => metadata.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Genres.Remove(genre);
                if (await _metadataRepository.RemoveGenre(genre))
                {
                    updated = true;
                }
            }

            foreach (Genre genre in metadata.Genres.Filter(g => existing.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Genres.Add(genre);
                if (await _artistRepository.AddGenre(existing, genre))
                {
                    updated = true;
                }
            }

            foreach (Style style in existing.Styles.Filter(s => metadata.Styles.All(s2 => s2.Name != s.Name))
                         .ToList())
            {
                existing.Styles.Remove(style);
                if (await _metadataRepository.RemoveStyle(style))
                {
                    updated = true;
                }
            }

            foreach (Style style in metadata.Styles.Filter(s => existing.Styles.All(s2 => s2.Name != s.Name))
                         .ToList())
            {
                existing.Styles.Add(style);
                if (await _artistRepository.AddStyle(existing, style))
                {
                    updated = true;
                }
            }

            foreach (Mood mood in existing.Moods.Filter(m => metadata.Moods.All(m2 => m2.Name != m.Name))
                         .ToList())
            {
                existing.Moods.Remove(mood);
                if (await _metadataRepository.RemoveMood(mood))
                {
                    updated = true;
                }
            }

            foreach (Mood mood in metadata.Moods.Filter(s => existing.Moods.All(m2 => m2.Name != s.Name))
                         .ToList())
            {
                existing.Moods.Add(mood);
                if (await _artistRepository.AddMood(existing, mood))
                {
                    updated = true;
                }
            }

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.ArtistId = artist.Id;
        artist.ArtistMetadata = [metadata];

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(MusicVideo musicVideo, MusicVideoMetadata metadata)
    {
        Option<MusicVideoMetadata> maybeMetadata = Optional(musicVideo.MusicVideoMetadata).Flatten().HeadOrNone();
        foreach (MusicVideoMetadata existing in maybeMetadata)
        {
            existing.Title = metadata.Title;
            existing.Year = metadata.Year;
            existing.Plot = metadata.Plot;
            existing.Track = metadata.Track;
            existing.Album = metadata.Album;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                _musicVideoRepository.AddGenre,
                _musicVideoRepository.AddTag,
                _musicVideoRepository.AddStudio,
                (_, _) => Task.FromResult(false));

            foreach (MusicVideoArtist artist in existing.Artists
                         .Filter(g => metadata.Artists.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Artists.Remove(artist);
                if (await _musicVideoRepository.RemoveArtist(artist))
                {
                    updated = true;
                }
            }

            foreach (MusicVideoArtist artist in metadata.Artists
                         .Filter(g => existing.Artists.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Artists.Add(artist);
                if (await _musicVideoRepository.AddArtist(existing, artist))
                {
                    updated = true;
                }
            }

            foreach (Director director in existing.Directors
                         .Filter(d => metadata.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Remove(director);
                if (await _metadataRepository.RemoveDirector(director))
                {
                    updated = true;
                }
            }

            foreach (Director director in metadata.Directors
                         .Filter(d => existing.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Add(director);
                if (await _musicVideoRepository.AddDirector(existing, director))
                {
                    updated = true;
                }
            }

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.MusicVideoId = musicVideo.Id;
        musicVideo.MusicVideoMetadata = new List<MusicVideoMetadata> { metadata };

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(OtherVideo otherVideo, OtherVideoMetadata metadata)
    {
        Option<OtherVideoMetadata> maybeMetadata = Optional(otherVideo.OtherVideoMetadata).Flatten().HeadOrNone();
        foreach (OtherVideoMetadata existing in maybeMetadata)
        {
            existing.ContentRating = metadata.ContentRating;
            existing.Outline = metadata.Outline;
            existing.Plot = metadata.Plot;
            existing.Tagline = metadata.Tagline;
            existing.Title = metadata.Title;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.Year = metadata.Year;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;
            existing.OriginalTitle = metadata.OriginalTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                _otherVideoRepository.AddGenre,
                _otherVideoRepository.AddTag,
                _otherVideoRepository.AddStudio,
                _otherVideoRepository.AddActor);

            foreach (Director director in existing.Directors
                         .Filter(d => metadata.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Remove(director);
                if (await _metadataRepository.RemoveDirector(director))
                {
                    updated = true;
                }
            }

            foreach (Director director in metadata.Directors
                         .Filter(d => existing.Directors.All(d2 => d2.Name != d.Name)).ToList())
            {
                existing.Directors.Add(director);
                if (await _otherVideoRepository.AddDirector(existing, director))
                {
                    updated = true;
                }
            }

            foreach (Writer writer in existing.Writers
                         .Filter(w => metadata.Writers.All(w2 => w2.Name != w.Name)).ToList())
            {
                existing.Writers.Remove(writer);
                if (await _metadataRepository.RemoveWriter(writer))
                {
                    updated = true;
                }
            }

            foreach (Writer writer in metadata.Writers
                         .Filter(w => existing.Writers.All(w2 => w2.Name != w.Name)).ToList())
            {
                existing.Writers.Add(writer);
                if (await _otherVideoRepository.AddWriter(existing, writer))
                {
                    updated = true;
                }
            }

            foreach (MetadataGuid guid in existing.Guids
                         .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Remove(guid);
                if (await _metadataRepository.RemoveGuid(guid))
                {
                    updated = true;
                }
            }

            foreach (MetadataGuid guid in metadata.Guids
                         .Filter(g => existing.Guids.All(g2 => g2.Guid != g.Guid)).ToList())
            {
                existing.Guids.Add(guid);
                if (await _metadataRepository.AddGuid(existing, guid))
                {
                    updated = true;
                }
            }

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.OtherVideoId = otherVideo.Id;
        otherVideo.OtherVideoMetadata = new List<OtherVideoMetadata> { metadata };

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(Song song, SongMetadata metadata)
    {
        Option<SongMetadata> maybeMetadata = Optional(song.SongMetadata).Flatten().HeadOrNone();
        foreach (SongMetadata existing in maybeMetadata)
        {
            existing.Title = metadata.Title;
            existing.Artists = metadata.Artists;
            existing.AlbumArtists = metadata.AlbumArtists;
            existing.Album = metadata.Album;
            existing.Track = metadata.Track;
            existing.Comment = metadata.Comment;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;
            existing.OriginalTitle = metadata.OriginalTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                _songRepository.AddGenre,
                _songRepository.AddTag,
                (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult(false));

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.SongId = song.Id;
        song.SongMetadata = new List<SongMetadata> { metadata };

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(Image image, ImageMetadata metadata)
    {
        Option<ImageMetadata> maybeMetadata = Optional(image.ImageMetadata).Flatten().HeadOrNone();
        foreach (ImageMetadata existing in maybeMetadata)
        {
            existing.DurationSeconds = metadata.DurationSeconds;
            existing.Title = metadata.Title;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.Year = metadata.Year;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;
            existing.OriginalTitle = metadata.OriginalTitle;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                (_, _) => Task.FromResult(false),
                _imageRepository.AddTag,
                (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult(false));

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.ImageId = image.Id;
        image.ImageMetadata = [metadata];

        return await _metadataRepository.Add(metadata);
    }

    private async Task<bool> ApplyMetadataUpdate(
        RemoteStream remoteStream,
        RemoteStreamMetadata metadata,
        CancellationToken cancellationToken)
    {
        Option<RemoteStreamMetadata> maybeMetadata = Optional(remoteStream.RemoteStreamMetadata).Flatten().HeadOrNone();
        foreach (RemoteStreamMetadata existing in maybeMetadata)
        {
            existing.Title = metadata.Title;

            if (existing.DateAdded == SystemTime.MinValueUtc)
            {
                existing.DateAdded = metadata.DateAdded;
            }

            existing.DateUpdated = metadata.DateUpdated;
            existing.MetadataKind = metadata.MetadataKind;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.ReleaseDate = metadata.ReleaseDate;
            existing.Year = metadata.Year;
            existing.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
                ? SortTitle.GetSortTitle(metadata.Title)
                : metadata.SortTitle;
            existing.OriginalTitle = metadata.OriginalTitle;
            existing.Plot = metadata.Plot;
            existing.ContentRating = metadata.ContentRating;

            bool updated = await UpdateMetadataCollections(
                existing,
                metadata,
                (_, _) => Task.FromResult(false),
                (metadata1, tag) => _remoteStreamRepository.AddTag(metadata1, tag, cancellationToken),
                (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult(false));

            return await _metadataRepository.Update(existing) || updated;
        }

        metadata.SortTitle = string.IsNullOrWhiteSpace(metadata.SortTitle)
            ? SortTitle.GetSortTitle(metadata.Title)
            : metadata.SortTitle;
        metadata.RemoteStreamId = remoteStream.Id;
        remoteStream.RemoteStreamMetadata = [metadata];

        return await _metadataRepository.Add(metadata);
    }

    private async Task<Option<ShowMetadata>> LoadTelevisionShowMetadata(string nfoFileName)
    {
        try
        {
            Either<BaseError, ShowNfo> maybeNfo = await _showNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read TvShow nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());
            }

            foreach (ShowNfo nfo in maybeNfo.RightToSeq())
            {
                DateTime dateAdded = DateTime.UtcNow;
                DateTime dateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName);

                return new ShowMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = dateAdded,
                    DateUpdated = dateUpdated,
                    Title = nfo.Title,
                    Plot = nfo.Plot,
                    Outline = nfo.Outline,
                    Tagline = nfo.Tagline,
                    ContentRating = nfo.ContentRating,
                    Year = GetYear(nfo.Year, nfo.Premiered),
                    ReleaseDate = GetAired(nfo.Year, nfo.Premiered),
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Tags = nfo.Tags.Map(t => new Tag { Name = t }).ToList(),
                    Studios = nfo.Studios.Map(s => new Studio { Name = s }).ToList(),
                    Actors = Actors(nfo.Actors, dateAdded, dateUpdated),
                    Guids = nfo.UniqueIds
                        .Map(id => new MetadataGuid { Guid = $"{id.Type}://{id.Guid}" })
                        .ToList(),
                    Artwork = []
                };
            }

            return None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read TV show nfo metadata from {Path}", nfoFileName);
            return None;
        }
    }

    private async Task<Option<ArtistMetadata>> LoadArtistMetadata(string nfoFileName)
    {
        try
        {
            Either<BaseError, ArtistNfo> maybeNfo = await _artistNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read Artist nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());
            }

            foreach (ArtistNfo nfo in maybeNfo.RightToSeq())
            {
                return new ArtistMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName),
                    Title = nfo.Name,
                    Disambiguation = nfo.Disambiguation,
                    Biography = nfo.Biography,
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Styles = nfo.Styles.Map(s => new Style { Name = s }).ToList(),
                    Moods = nfo.Moods.Map(m => new Mood { Name = m }).ToList(),
                    Artwork = []
                };
            }

            return None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read artist nfo metadata from {Path}", nfoFileName);
            return None;
        }
    }

    private async Task<List<EpisodeMetadata>> LoadEpisodeMetadata(Episode episode, string nfoFileName)
    {
        try
        {
            Either<BaseError, List<EpisodeNfo>> maybeNfo = await _episodeNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read Episode nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());
            }

            var result = new List<EpisodeMetadata>();
            foreach (EpisodeNfo nfo in maybeNfo.RightToSeq().Flatten())
            {
                DateTime dateAdded = DateTime.UtcNow;
                DateTime dateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName);

                var metadata = new EpisodeMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = dateAdded,
                    DateUpdated = dateUpdated,
                    Title = nfo.Title,
                    SortTitle = SortTitle.GetSortTitle(nfo.Title),
                    EpisodeNumber = nfo.Episode,
                    Year = GetYear(0, nfo.Aired),
                    ReleaseDate = GetAired(0, nfo.Aired),
                    Plot = nfo.Plot,
                    Actors = Actors(nfo.Actors, dateAdded, dateUpdated),
                    Guids = nfo.UniqueIds
                        .Map(id => new MetadataGuid { Guid = $"{id.Type}://{id.Guid}" })
                        .ToList(),
                    Directors = nfo.Directors.Map(d => new Director { Name = d }).ToList(),
                    Writers = nfo.Writers.Map(w => new Writer { Name = w }).ToList(),
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Tags = nfo.Tags.Map(t => new Tag { Name = t }).ToList(),
                    Studios = [],
                    Artwork = []
                };

                result.Add(metadata);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read TV episode nfo metadata from {Path}", nfoFileName);
            return _fallbackMetadataProvider.GetFallbackMetadata(episode);
        }
    }

    private async Task<Option<MovieMetadata>> LoadMovieMetadata(Movie movie, string nfoFileName)
    {
        try
        {
            Either<BaseError, MovieNfo> maybeNfo = await _movieNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read Movie nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());

                return _fallbackMetadataProvider.GetFallbackMetadata(movie);
            }

            foreach (MovieNfo nfo in maybeNfo.RightToSeq())
            {
                DateTime dateAdded = DateTime.UtcNow;
                DateTime dateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName);

                var year = 0;
                if (nfo.Year > 1000)
                {
                    year = nfo.Year;
                }

                DateTime releaseDate = year > 0
                    ? new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime
                    : SystemTime.MinValueUtc;

                foreach (DateTime premiered in nfo.Premiered)
                {
                    if (year == 0)
                    {
                        year = premiered.Year;
                    }

                    releaseDate = premiered;
                }

                var tags = nfo.Tags.Map(t => new Tag { Name = t }).ToList();
                foreach (string country in nfo.Countries)
                {
                    tags.Add(
                        new Tag
                        {
                            Name = country,
                            ExternalTypeId = Tag.NfoCountryTypeId
                        });
                }

                return new MovieMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = dateAdded,
                    DateUpdated = dateUpdated,
                    Title = nfo.Title,
                    SortTitle = nfo.SortTitle,
                    Year = year,
                    ContentRating = nfo.ContentRating,
                    ReleaseDate = releaseDate,
                    Plot = nfo.Plot,
                    Outline = nfo.Outline,
                    // Tagline = nfo.Tagline,
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Tags = tags,
                    Studios = nfo.Studios.Map(s => new Studio { Name = s }).ToList(),
                    Actors = Actors(nfo.Actors, dateAdded, dateUpdated),
                    Directors = nfo.Directors.Map(d => new Director { Name = d }).ToList(),
                    Writers = nfo.Writers.Map(w => new Writer { Name = w }).ToList(),
                    Guids = nfo.UniqueIds
                        .Map(id => new MetadataGuid { Guid = $"{id.Type}://{id.Guid}" })
                        .ToList(),
                    Artwork = []
                };
            }

            return None;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read Movie nfo metadata from {Path}", nfoFileName);
            return _fallbackMetadataProvider.GetFallbackMetadata(movie);
        }
    }

    private async Task<Option<OtherVideoMetadata>> LoadOtherVideoMetadata(string nfoFileName)
    {
        try
        {
            Either<BaseError, OtherVideoNfo> maybeNfo = await _otherVideoNfoReader.ReadFromFile(nfoFileName);
            foreach (BaseError error in maybeNfo.LeftToSeq())
            {
                _logger.LogInformation(
                    "Failed to read OtherVideo nfo metadata from {Path}: {Error}",
                    nfoFileName,
                    error.ToString());

                return None;
            }

            foreach (OtherVideoNfo nfo in maybeNfo.RightToSeq())
            {
                DateTime dateAdded = DateTime.UtcNow;
                DateTime dateUpdated = _fileSystem.File.GetLastWriteTimeUtc(nfoFileName);

                var year = 0;
                if (nfo.Year > 1000)
                {
                    year = nfo.Year;
                }

                DateTime releaseDate = year > 0
                    ? new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime
                    : SystemTime.MinValueUtc;

                foreach (DateTime premiered in nfo.Premiered)
                {
                    if (year == 0)
                    {
                        year = premiered.Year;
                    }

                    releaseDate = premiered;
                }

                return new OtherVideoMetadata
                {
                    MetadataKind = MetadataKind.Sidecar,
                    DateAdded = dateAdded,
                    DateUpdated = dateUpdated,
                    Title = nfo.Title,
                    SortTitle = nfo.SortTitle,
                    Year = year,
                    ContentRating = nfo.ContentRating,
                    ReleaseDate = releaseDate,
                    Plot = nfo.Plot,
                    Outline = nfo.Outline,
                    Tagline = nfo.Tagline,
                    Genres = nfo.Genres.Map(g => new Genre { Name = g }).ToList(),
                    Tags = nfo.Tags.Map(t => new Tag { Name = t }).ToList(),
                    Studios = nfo.Studios.Map(s => new Studio { Name = s }).ToList(),
                    Actors = Actors(nfo.Actors, dateAdded, dateUpdated),
                    Directors = nfo.Directors.Map(d => new Director { Name = d }).ToList(),
                    Writers = nfo.Writers.Map(w => new Writer { Name = w }).ToList(),
                    Guids = nfo.UniqueIds
                        .Map(id => new MetadataGuid { Guid = $"{id.Type}://{id.Guid}" })
                        .ToList(),
                    Artwork = []
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to read OtherVideo nfo metadata from {Path}", nfoFileName);
        }

        return None;
    }

    private static int? GetYear(int? year, Option<DateTime> premiered)
    {
        if (year is > 1000)
        {
            return year;
        }

        foreach (DateTime p in premiered)
        {
            return p.Year;
        }

        return null;
    }

    private static DateTime? GetAired(int? year, Option<DateTime> aired)
    {
        DateTime? fallback = year is > 1000 ? new DateTime(year.Value, 1, 1) : null;

        foreach (DateTime a in aired)
        {
            return a;
        }

        return fallback;
    }

    private async Task<bool> UpdateMetadataCollections<T>(
        T existing,
        T incoming,
        Func<T, Genre, Task<bool>> addGenre,
        Func<T, Tag, Task<bool>> addTag,
        Func<T, Studio, Task<bool>> addStudio,
        Func<T, Actor, Task<bool>> addActor)
        where T : ErsatzTV.Core.Domain.Metadata
    {
        var updated = false;

        if (existing is not EpisodeMetadata)
        {
            foreach (Genre genre in existing.Genres.Filter(g => incoming.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Genres.Remove(genre);
                if (await _metadataRepository.RemoveGenre(genre))
                {
                    updated = true;
                }
            }

            foreach (Genre genre in incoming.Genres.Filter(g => existing.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                existing.Genres.Add(genre);
                if (await addGenre(existing, genre))
                {
                    updated = true;
                }
            }

            // an older version did not save the type id; the comparison uses the type id,
            // so a scan replaces such a row
            foreach (Tag tag in existing.Tags
                         .Filter(t => incoming.Tags.All(t2 => t2.Name != t.Name || t2.ExternalTypeId != t.ExternalTypeId))
                         .ToList())
            {
                existing.Tags.Remove(tag);
                if (await _metadataRepository.RemoveTag(tag))
                {
                    updated = true;
                }
            }

            foreach (Tag tag in incoming.Tags
                         .Filter(t => existing.Tags.All(t2 => t2.Name != t.Name || t2.ExternalTypeId != t.ExternalTypeId))
                         .ToList())
            {
                existing.Tags.Add(tag);
                if (await addTag(existing, tag))
                {
                    updated = true;
                }
            }

            foreach (Studio studio in existing.Studios
                         .Filter(s => incoming.Studios.All(s2 => s2.Name != s.Name))
                         .ToList())
            {
                existing.Studios.Remove(studio);
                if (await _metadataRepository.RemoveStudio(studio))
                {
                    updated = true;
                }
            }

            foreach (Studio studio in incoming.Studios
                         .Filter(s => existing.Studios.All(s2 => s2.Name != s.Name))
                         .ToList())
            {
                existing.Studios.Add(studio);
                if (await addStudio(existing, studio))
                {
                    updated = true;
                }
            }
        }

        if (existing is not MusicVideoMetadata and not SongMetadata)
        {
            foreach (Actor actor in existing.Actors
                         .Filter(a => incoming.Actors.All(a2 => a2.Name != a.Name))
                         .ToList())
            {
                existing.Actors.Remove(actor);
                if (await _metadataRepository.RemoveActor(actor))
                {
                    updated = true;
                }
            }

            foreach (Actor actor in incoming.Actors
                         .Filter(a => existing.Actors.All(a2 => a2.Name != a.Name))
                         .ToList())
            {
                existing.Actors.Add(actor);
                if (await addActor(existing, actor))
                {
                    updated = true;
                }
            }
        }

        return updated;
    }

    private static List<Actor> Actors(List<ActorNfo> actorNfos, DateTime dateAdded, DateTime dateUpdated)
    {
        var result = new List<Actor>();

        for (var i = 0; i < actorNfos.Count; i++)
        {
            ActorNfo actorNfo = actorNfos[i];

            var actor = new Actor
            {
                Name = actorNfo.Name,
                Role = actorNfo.Role,
                Order = actorNfo.Order ?? i
            };

            if (!string.IsNullOrWhiteSpace(actorNfo.Thumb))
            {
                actor.Artwork = new Artwork
                {
                    Path = actorNfo.Thumb,
                    ArtworkKind = ArtworkKind.Thumbnail,
                    DateAdded = dateAdded,
                    DateUpdated = dateUpdated
                };
            }

            result.Add(actor);
        }

        return result;
    }
}
