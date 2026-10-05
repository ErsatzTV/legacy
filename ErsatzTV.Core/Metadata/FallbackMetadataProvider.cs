using System.Globalization;
using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;

namespace ErsatzTV.Core.Metadata;

public partial class FallbackMetadataProvider : IFallbackMetadataProvider
{
    private static readonly Regex SeasonPattern = SeasonNumber();

    public Option<int> GetSeasonNumberForFolder(string folder)
    {
        string folderName = Path.GetFileName(folder) ?? folder;
        if (int.TryParse(folderName, out int seasonNumber))
        {
            return seasonNumber;
        }

        Match match = SeasonPattern.Match(folderName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out seasonNumber))
        {
            return seasonNumber;
        }

        if (int.TryParse(folder.Split(" ").Last(), out seasonNumber))
        {
            return seasonNumber;
        }

        if (folder.EndsWith("specials", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return None;
    }

    public ShowMetadata GetFallbackMetadataForShow(string showFolder)
    {
        string fileName = Path.GetFileName(showFolder);
        var metadata = new ShowMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? showFolder,
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Artwork = [],
            Guids = []
        };
        return GetTelevisionShowMetadata(fileName, metadata);
    }

    public ArtistMetadata GetFallbackMetadataForArtist(string artistFolder)
    {
        string fileName = Path.GetFileName(artistFolder);
        return new ArtistMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? artistFolder,
            Artwork = [],
            Genres = [],
            Styles = [],
            Moods = []
        };
    }

    public List<EpisodeMetadata> GetFallbackMetadata(Episode episode)
    {
        string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileName(path);
        var baseMetadata = new EpisodeMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = Path.GetFileNameWithoutExtension(path) ?? path,
            DateAdded = DateTime.UtcNow,
            EpisodeNumber = 0,
            Actors = [],
            Artwork = [],
            Directors = [],
            Genres = [],
            Guids = [],
            Studios = [],
            Tags = [],
            Writers = []
        };
        return fileName != null
            ? GetEpisodeMetadata(fileName, baseMetadata)
            : [baseMetadata];
    }

    public MovieMetadata GetFallbackMetadata(Movie movie)
    {
        string path = movie.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileName(path);
        var metadata = new MovieMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = Path.GetFileNameWithoutExtension(path) ?? path,
            Artwork = [],
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Directors = [],
            Writers = [],
            Guids = []
        };

        return fileName != null ? GetMovieMetadata(fileName, metadata) : metadata;
    }

    public Option<MusicVideoMetadata> GetFallbackMetadata(MusicVideo musicVideo)
    {
        string path = musicVideo.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileName(path);
        var metadata = new MusicVideoMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? path,
            Artwork = []
        };

        return GetMusicVideoMetadata(fileName, metadata);
    }

    public Option<OtherVideoMetadata> GetFallbackMetadata(OtherVideo otherVideo)
    {
        string path = otherVideo.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileNameWithoutExtension(path);
        var metadata = new OtherVideoMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? path,
            OtherVideo = otherVideo,
            Artwork = [],
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Directors = [],
            Writers = [],
            Guids = []
        };

        return GetOtherVideoMetadata(path, metadata);
    }

    public Option<SongMetadata> GetFallbackMetadata(Song song)
    {
        string path = song.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileNameWithoutExtension(path);
        var metadata = new SongMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? path,
            Song = song,
            Artwork = []
        };

        return GetSongMetadata(path, metadata);
    }

    public Option<ImageMetadata> GetFallbackMetadata(Image image)
    {
        string path = image.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileNameWithoutExtension(path);
        var metadata = new ImageMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? path,
            Image = image,
            Artwork = [],
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Guids = []
        };

        return GetImageMetadata(path, metadata);
    }

    public Option<RemoteStreamMetadata> GetFallbackMetadata(RemoteStream remoteStream)
    {
        string path = remoteStream.MediaVersions.Head().MediaFiles.Head().Path;
        string fileName = Path.GetFileNameWithoutExtension(path);
        var metadata = new RemoteStreamMetadata
        {
            MetadataKind = MetadataKind.Fallback,
            Title = fileName ?? path,
            RemoteStream = remoteStream,
            Artwork = [],
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Guids = []
        };

        return GetRemoteStreamMetadata(path, metadata);
    }

    [GeneratedRegex(@"s(?:eason)?\s?(\d+)(?![e\d])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonNumber();

    private static List<EpisodeMetadata> GetEpisodeMetadata(string fileName, EpisodeMetadata baseMetadata)
    {
        var result = new List<EpisodeMetadata> { baseMetadata };

        try
        {
            const string PATTERN = @"[sS]\d+[\._xX]?[eE]([e\-\d{1,2}]+)";
            const string PATTERN_2 = @"\d+[\._xX]([e\-\d{1,2}]+)";
            MatchCollection matches = Regex.Matches(fileName, PATTERN);
            if (matches.Count == 0)
            {
                matches = Regex.Matches(fileName, PATTERN_2);
            }

            if (matches.Count > 0)
            {
                var episodeNumbers = matches.Bind(m => m.Groups[1].Value
                        .Replace('e', '-')
                        .Split('-')
                        .Bind(ep => int.TryParse(ep, out int num) ? Some(num) : Option<int>.None))
                    .ToList();

                switch (episodeNumbers.Count)
                {
                    case 0:
                        break;
                    case 1:
                        baseMetadata.EpisodeNumber = episodeNumbers.Head();
                        break;
                    default:
                        result.Clear();
                        foreach (int episodeNumber in episodeNumbers)
                        {
                            var metadata = new EpisodeMetadata
                            {
                                MetadataKind = MetadataKind.Fallback,
                                EpisodeNumber = episodeNumber,
                                DateAdded = baseMetadata.DateAdded,
                                DateUpdated = baseMetadata.DateAdded,
                                Title = baseMetadata.Title,
                                Actors = [],
                                Artwork = [],
                                Directors = [],
                                Genres = [],
                                Guids = [],
                                Studios = [],
                                Tags = [],
                                Writers = []
                            };

                            result.Add(metadata);
                        }

                        break;
                }
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return result;
    }

    private static MovieMetadata GetMovieMetadata(string fileName, MovieMetadata metadata)
    {
        try
        {
            const string PATTERN = @"^(.*?)[.\(](\d{4})[.\)].*\.\w+$";
            Match match = Regex.Match(fileName, PATTERN);
            if (match.Success)
            {
                metadata.Title = match.Groups[1].Value.Trim();
                metadata.Year = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                metadata.ReleaseDate =
                    new DateTime(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 1, 1);
                metadata.DateUpdated = DateTime.UtcNow;
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return metadata;
    }

    private static Option<MusicVideoMetadata> GetMusicVideoMetadata(string fileName, MusicVideoMetadata metadata)
    {
        try
        {
            const string PATTERN = @"^(.*?) - (.*?).\w+$";
            Match match = Regex.Match(fileName, PATTERN);
            metadata.Title = match.Success
                ? match.Groups[2].Value.Trim()
                : Path.GetFileNameWithoutExtension(fileName);
            metadata.Artists = [];
            metadata.Artwork = [];
            metadata.Genres = [];
            metadata.Tags = [];
            metadata.Studios = [];
            metadata.Directors = [];
            metadata.DateUpdated = DateTime.UtcNow;

            return metadata;
        }
        catch (Exception)
        {
            return None;
        }
    }

    private static Option<OtherVideoMetadata> GetOtherVideoMetadata(string path, OtherVideoMetadata metadata)
    {
        try
        {
            string folder = Path.GetDirectoryName(path);
            if (folder == null)
            {
                return None;
            }

            string libraryPath = metadata.OtherVideo.LibraryPath.Path;
            string parent = Optional(Directory.GetParent(libraryPath)).Match(
                di => di.FullName,
                () => libraryPath);

            string diff = Path.GetRelativePath(parent, folder);

            var tags = diff.Split(Path.DirectorySeparatorChar)
                .Map(t => new Tag { Name = t })
                .ToList();

            metadata.Actors = [];
            metadata.Artwork = [];
            metadata.Genres = [];
            metadata.Tags = tags;
            metadata.Studios = [];
            metadata.DateUpdated = DateTime.UtcNow;
            metadata.OriginalTitle = Path.GetRelativePath(libraryPath, path);

            return metadata;
        }
        catch (Exception)
        {
            return None;
        }
    }

    private static Option<ImageMetadata> GetImageMetadata(string path, ImageMetadata metadata)
    {
        try
        {
            string folder = Path.GetDirectoryName(path);
            if (folder == null)
            {
                return None;
            }

            string libraryPath = metadata.Image.LibraryPath.Path;
            string parent = Optional(Directory.GetParent(libraryPath)).Match(
                di => di.FullName,
                () => libraryPath);

            string diff = Path.GetRelativePath(parent, folder);

            var tags = diff.Split(Path.DirectorySeparatorChar)
                .Map(t => new Tag { Name = t })
                .ToList();

            metadata.Actors = [];
            metadata.Artwork = [];
            metadata.Genres = [];
            metadata.Tags = tags;
            metadata.Studios = [];
            metadata.DateUpdated = DateTime.UtcNow;
            metadata.OriginalTitle = Path.GetRelativePath(libraryPath, path);

            return metadata;
        }
        catch (Exception)
        {
            return None;
        }
    }

    private static Option<RemoteStreamMetadata> GetRemoteStreamMetadata(string path, RemoteStreamMetadata metadata)
    {
        try
        {
            string folder = Path.GetDirectoryName(path);
            if (folder == null)
            {
                return None;
            }

            string libraryPath = metadata.RemoteStream.LibraryPath.Path;
            string parent = Optional(Directory.GetParent(libraryPath)).Match(
                di => di.FullName,
                () => libraryPath);

            string diff = Path.GetRelativePath(parent, folder);

            var tags = diff.Split(Path.DirectorySeparatorChar)
                .Map(t => new Tag { Name = t })
                .ToList();

            metadata.Actors = [];
            metadata.Artwork = [];
            metadata.Genres = [];
            metadata.Tags = tags;
            metadata.Studios = [];
            metadata.DateUpdated = DateTime.UtcNow;
            metadata.OriginalTitle = Path.GetRelativePath(libraryPath, path);

            return metadata;
        }
        catch (Exception)
        {
            return None;
        }
    }

    private static Option<SongMetadata> GetSongMetadata(string path, SongMetadata metadata)
    {
        try
        {
            string folder = Path.GetDirectoryName(path);
            if (folder == null)
            {
                return None;
            }

            string libraryPath = metadata.Song.LibraryPath.Path;
            string parent = Optional(Directory.GetParent(libraryPath)).Match(
                di => di.FullName,
                () => libraryPath);

            string diff = Path.GetRelativePath(parent, folder);

            var tags = diff.Split(Path.DirectorySeparatorChar)
                .Map(t => new Tag { Name = t })
                .ToList();

            metadata.Actors = [];
            metadata.Artwork = [];
            metadata.Genres = [];
            metadata.Tags = tags;
            metadata.Studios = [];
            metadata.DateUpdated = DateTime.UtcNow;
            metadata.OriginalTitle = Path.GetRelativePath(libraryPath, path);

            return metadata;
        }
        catch (Exception)
        {
            return None;
        }
    }

    private static ShowMetadata GetTelevisionShowMetadata(string fileName, ShowMetadata metadata)
    {
        try
        {
            const string PATTERN = @"^(.*?)[\s.]*?[.\(](\d{4})[.\)].*$";
            Match match = Regex.Match(fileName, PATTERN);
            if (match.Success)
            {
                metadata.Title = match.Groups[1].Value;
                metadata.Year = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                metadata.ReleaseDate =
                    new DateTime(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 1, 1);
                metadata.DateUpdated = DateTime.UtcNow;
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return metadata;
    }
}
