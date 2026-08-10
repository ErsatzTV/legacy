using System.Globalization;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure.Jellyfin.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Refit;

namespace ErsatzTV.Infrastructure.Jellyfin;

public class JellyfinApiClient : IJellyfinApiClient
{
    private readonly IFallbackMetadataProvider _fallbackMetadataProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJellyfinPathReplacementService _jellyfinPathReplacementService;
    private readonly ILogger<JellyfinApiClient> _logger;
    private readonly IMemoryCache _memoryCache;

    public JellyfinApiClient(
        IMemoryCache memoryCache,
        IJellyfinPathReplacementService jellyfinPathReplacementService,
        IFallbackMetadataProvider fallbackMetadataProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<JellyfinApiClient> logger)
    {
        _memoryCache = memoryCache;
        _jellyfinPathReplacementService = jellyfinPathReplacementService;
        _fallbackMetadataProvider = fallbackMetadataProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, JellyfinServerInformation>> GetServerInformation(
        string address,
        string authorizationHeader)
    {
        try
        {
            IJellyfinApi service = ServiceForAddress(address);
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await service.GetSystemInformation(authorizationHeader, cts.Token)
                .Map(response => new JellyfinServerInformation(response.ServerName, response.OperatingSystem));
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogError(ex, "Timeout getting jellyfin server name");
            return BaseError.New("Jellyfin did not respond in time");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting jellyfin server name");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<JellyfinLibrary>>> GetLibraries(string address, string authorizationHeader)
    {
        try
        {
            IJellyfinApi service = ServiceForAddress(address);
            List<JellyfinLibraryResponse> libraries = await service.GetLibraries(authorizationHeader);
            return libraries
                .Map(Project)
                .Somes()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting jellyfin libraries");
            return BaseError.New(ex.Message);
        }
    }

    public IAsyncEnumerable<Tuple<JellyfinMovie, int>> GetMovieLibraryItems(
        string address,
        string authorizationHeader,
        JellyfinLibrary library) =>
        GetPagedLibraryItems(
            "JF Movies",
            address,
            library,
            library.MediaSourceId,
            library.ItemId,
            (service, itemId, skip, pageSize) => service.GetMovieLibraryItems(
                authorizationHeader,
                itemId,
                startIndex: skip,
                limit: pageSize),
            (maybeLibrary, item) => maybeLibrary.Map(lib => ProjectToMovie(lib, item)).Flatten());

    public IAsyncEnumerable<Tuple<JellyfinShow, int>> GetShowLibraryItemsWithoutPeople(
        string address,
        string authorizationHeader,
        JellyfinLibrary library) =>
        GetPagedLibraryItems(
            "JF Shows",
            address,
            library,
            library.MediaSourceId,
            library.ItemId,
            (service, itemId, skip, pageSize) => service.GetShowLibraryItemsWithoutPeople(
                authorizationHeader,
                itemId,
                startIndex: skip,
                limit: pageSize),
            (_, item) => ProjectToShow(item));

    public IAsyncEnumerable<Tuple<JellyfinSeason, int>> GetSeasonLibraryItems(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string showId) =>
        GetPagedLibraryItems(
            "JF Seasons",
            address,
            library,
            library.MediaSourceId,
            showId,
            (service, _, skip, pageSize) => service.GetSeasonLibraryItems(
                authorizationHeader,
                showId,
                startIndex: skip,
                limit: pageSize),
            (_, item) => ProjectToSeason(item));

    public IAsyncEnumerable<Tuple<JellyfinEpisode, int>> GetEpisodeLibraryItems(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string seasonId) =>
        GetPagedLibraryItems(
            "JF Episodes*",
            address,
            library,
            library.MediaSourceId,
            seasonId,
            (service, _, skip, pageSize) => service.GetEpisodeLibraryItems(
                authorizationHeader,
                seasonId,
                startIndex: skip,
                limit: pageSize),
            (maybeLibrary, item) => maybeLibrary.Map(lib => ProjectToEpisode(lib, item)).Flatten());

    public IAsyncEnumerable<Tuple<JellyfinEpisode, int>> GetEpisodeLibraryItemsWithoutPeople(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string seasonId) =>
        GetPagedLibraryItems(
            "JF Episodes",
            address,
            library,
            library.MediaSourceId,
            seasonId,
            (service, _, skip, pageSize) => service.GetEpisodeLibraryItemsWithoutPeople(
                authorizationHeader,
                seasonId,
                startIndex: skip,
                limit: pageSize),
            (maybeLibrary, item) => maybeLibrary.Map(lib => ProjectToEpisode(lib, item)).Flatten());

    public IAsyncEnumerable<Tuple<JellyfinCollection, int>> GetCollectionLibraryItems(
        string address,
        string authorizationHeader,
        int mediaSourceId)
    {
        // TODO: should we enumerate collection libraries here?

        if (_memoryCache.TryGetValue("jellyfin_collections_library_item_id", out string itemId))
        {
            return GetPagedLibraryItems(
                "JF Collections",
                address,
                None,
                mediaSourceId,
                itemId,
                (service, _, skip, pageSize) => service.GetCollectionLibraryItems(
                    authorizationHeader,
                    itemId,
                    startIndex: skip,
                    limit: pageSize),
                (_, item) => ProjectToCollection(item));
        }

        return AsyncEnumerable.Empty<Tuple<JellyfinCollection, int>>();
    }

    public IAsyncEnumerable<Tuple<MediaItem, int>> GetCollectionItems(
        string address,
        string authorizationHeader,
        int mediaSourceId,
        string collectionId) =>
        GetPagedLibraryItems(
            "JF Collection Items",
            address,
            None,
            mediaSourceId,
            collectionId,
            (service, _, skip, pageSize) => service.GetCollectionItems(
                authorizationHeader,
                collectionId,
                startIndex: skip,
                limit: pageSize),
            (_, item) => ProjectToCollectionMediaItem(item));

    public async Task<Either<BaseError, MediaVersion>> GetPlaybackInfo(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string itemId)
    {
        try
        {
            using (ScanProfiler.Measure("JF Playback Info"))
            {
                IJellyfinApi service = ServiceForAddress(address);
                JellyfinPlaybackInfoResponse playbackInfo = await service.GetPlaybackInfo(authorizationHeader, itemId);
                Option<MediaVersion> maybeVersion = ProjectToMediaVersion(playbackInfo);
                return maybeVersion.ToEither(() => BaseError.New("Unable to locate Jellyfin statistics"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting jellyfin playback info");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, Option<JellyfinShow>>> GetSingleShow(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string showId)
    {
        try
        {
            using (ScanProfiler.Measure("JF Single Show"))
            {
                IJellyfinApi service = ServiceForAddress(address);
                JellyfinLibraryItemsResponse itemsResponse = await service.GetShowLibraryItems(
                    authorizationHeader,
                    parentId: library.ItemId,
                    recursive: false,
                    startIndex: 0,
                    limit: 1,
                    ids: showId);

                foreach (JellyfinLibraryItemResponse item in itemsResponse.Items)
                {
                    return ProjectToShow(item);
                }

                return BaseError.New($"Unable to locate show with id {showId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jellyfin shows by id");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<JellyfinShow>>> SearchShowsByTitle(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string showTitle)
    {
        try
        {
            IJellyfinApi service = ServiceForAddress(address);
            JellyfinSearchHintsResponse searchResponse = await service.SearchHints(
                authorizationHeader,
                showTitle,
                "Series",
                library.ItemId);

            var shows = new List<JellyfinShow>();

            foreach (JellyfinSearchHintResponse hint in searchResponse.SearchHints)
            {
                if (hint.Type == "Series" &&
                    string.Equals(hint.Name, showTitle, StringComparison.OrdinalIgnoreCase))
                {
                    JellyfinLibraryItemsResponse detailResponse = await service.GetShowLibraryItems(
                        authorizationHeader,
                        hint.Id,
                        recursive: false,
                        startIndex: 0,
                        limit: 1);

                    foreach (JellyfinLibraryItemResponse item in detailResponse.Items)
                    {
                        Option<JellyfinShow> maybeShow = ProjectToShow(item);
                        foreach (JellyfinShow show in maybeShow)
                        {
                            shows.Add(show);
                        }
                    }
                }
            }

            return shows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jellyfin shows by title");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, Option<JellyfinEpisode>>> GetSingleEpisode(
        string address,
        string authorizationHeader,
        JellyfinLibrary library,
        string seasonId,
        string episodeId)
    {
        try
        {
            using (ScanProfiler.Measure("JF Single Episode"))
            {
                IJellyfinApi service = ServiceForAddress(address);
                JellyfinLibraryItemsResponse itemsResponse = await service.GetEpisodeLibraryItems(
                    authorizationHeader,
                    parentId: seasonId,
                    recursive: false,
                    startIndex: 0,
                    limit: 1,
                    ids: episodeId);

                foreach (JellyfinLibraryItemResponse item in itemsResponse.Items)
                {
                    return ProjectToEpisode(library, item);
                }

                return BaseError.New($"Unable to locate episode with id {episodeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jellyfin episodes by id");
            return BaseError.New(ex.Message);
        }
    }

    private async IAsyncEnumerable<Tuple<TItem, int>> GetPagedLibraryItems<TItem>(
        string pageDescription,
        string address,
        Option<JellyfinLibrary> maybeLibrary,
        int mediaSourceId,
        string parentId,
        Func<IJellyfinApi, string, int, int, Task<JellyfinLibraryItemsResponse>> getItems,
        Func<Option<JellyfinLibrary>, JellyfinLibraryItemResponse, Option<TItem>> mapper)
    {
        IJellyfinApi service = ServiceForAddress(address);

        int pages = int.MaxValue;
        for (var i = 0; i < pages; i++)
        {
            int skip = i * SystemEnvironment.JellyfinPageSize;

            JellyfinLibraryItemsResponse result;

            using (ScanProfiler.Measure(pageDescription))
            {
                result = await getItems(
                    service,
                    parentId,
                    skip,
                    SystemEnvironment.JellyfinPageSize);
            }

            // update page count
            pages = Math.Min(pages, (result.TotalRecordCount - 1) / SystemEnvironment.JellyfinPageSize + 1);

            foreach (TItem item in result.Items.Map(item => mapper(maybeLibrary, item)).Somes())
            {
                yield return new Tuple<TItem, int>(item, result.TotalRecordCount);
            }
        }
    }

    private Option<MediaItem> ProjectToCollectionMediaItem(JellyfinLibraryItemResponse item)
    {
        try
        {
            if (item.LocationType != "FileSystem")
            {
                return None;
            }

            if (Path.GetExtension(item.Path)?.ToLowerInvariant() == ".strm")
            {
                _logger.LogWarning("STRM files are not supported; skipping {Path}", item.Path);
                return None;
            }

            return item.Type switch
            {
                "Movie" => new JellyfinMovie { ItemId = item.Id },
                "Series" => new JellyfinShow { ItemId = item.Id },
                "Season" => new JellyfinSeason { ItemId = item.Id },
                "Episode" => new JellyfinEpisode { ItemId = item.Id },
                _ => None
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin collection media item");
            return None;
        }
    }

    private Option<JellyfinLibrary> Project(JellyfinLibraryResponse response) =>
        response.CollectionType?.ToLowerInvariant() switch
        {
            "tvshows" => new JellyfinLibrary
            {
                ItemId = response.ItemId,
                Name = response.Name,
                MediaKind = LibraryMediaKind.Shows,
                ShouldSyncItems = false,
                Paths = new List<LibraryPath> { new() { Path = $"jellyfin://{response.ItemId}" } },
                PathInfos = GetPathInfos(response)
            },
            "movies" => new JellyfinLibrary
            {
                ItemId = response.ItemId,
                Name = response.Name,
                MediaKind = LibraryMediaKind.Movies,
                ShouldSyncItems = false,
                Paths = new List<LibraryPath> { new() { Path = $"jellyfin://{response.ItemId}" } },
                PathInfos = GetPathInfos(response)
            },
            // TODO: ??? for music libraries
            "boxsets" => CacheCollectionLibraryId(response.ItemId),
            _ => None
        };

    private static List<JellyfinPathInfo> GetPathInfos(JellyfinLibraryResponse response)
    {
        var result = new List<JellyfinPathInfo>();

        if (response.LibraryOptions?.PathInfos is not null)
        {
            result.AddRange(
                response.LibraryOptions.PathInfos
                    .Filter(pi => !string.IsNullOrWhiteSpace(pi.NetworkPath))
                    .Map(pi => new JellyfinPathInfo
                    {
                        Path = pi.Path,
                        NetworkPath = pi.NetworkPath
                    }));
        }

        return result;
    }

    private Option<JellyfinLibrary> CacheCollectionLibraryId(string itemId)
    {
        _memoryCache.Set("jellyfin_collections_library_item_id", itemId);
        return None;
    }

    private Option<JellyfinMovie> ProjectToMovie(JellyfinLibrary library, JellyfinLibraryItemResponse item)
    {
        try
        {
            if (item.LocationType != "FileSystem")
            {
                return None;
            }

            if (Path.GetExtension(item.Path)?.ToLowerInvariant() == ".strm")
            {
                _logger.LogInformation("STRM files are not supported; skipping {Path}", item.Path);
                return None;
            }

            string path = item.Path ?? string.Empty;
            foreach (JellyfinPathInfo pathInfo in library.PathInfos.Filter(pi =>
                         !string.IsNullOrWhiteSpace(pi.NetworkPath)))
            {
                if (path.StartsWith(pathInfo.NetworkPath, StringComparison.Ordinal))
                {
                    path = _jellyfinPathReplacementService.ReplaceNetworkPath(
                        (JellyfinMediaSource)library.MediaSource,
                        path,
                        pathInfo.NetworkPath,
                        pathInfo.Path);
                }
            }

            var duration = TimeSpan.FromTicks(item.RunTimeTicks);
            var version = new MediaVersion
            {
                Name = "Main",
                Duration = duration,
                DateAdded = item.DateCreated.UtcDateTime,
                MediaFiles =
                [
                    new MediaFile
                    {
                        Path = path,
                        PathHash = PathUtils.GetPathHash(path)
                    }
                ],
                Streams = [],
                Chapters = ProjectToModel(Optional(item.Chapters).Flatten(), duration)
            };

            MovieMetadata metadata = ProjectToMovieMetadata(item);

            var movie = new JellyfinMovie
            {
                ItemId = item.Id,
                Etag = item.Etag,
                MediaVersions = [version],
                MovieMetadata = [metadata],
                TraktListItems = []
            };

            return movie;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin movie");
            return None;
        }
    }

    private static List<MediaChapter> ProjectToModel(
        IEnumerable<JellyfinChapterResponse> jellyfinChapters,
        TimeSpan duration)
    {
        var models = jellyfinChapters.Map(ProjectToModel).OrderBy(c => c.StartTime).ToList();

        for (var index = 0; index < models.Count; index++)
        {
            MediaChapter model = models[index];
            model.ChapterId = index;
            model.EndTime = index == models.Count - 1 ? duration : models[index + 1].StartTime;
        }

        return models;
    }

    private static MediaChapter ProjectToModel(JellyfinChapterResponse chapterResponse) =>
        new()
        {
            Title = chapterResponse.Name,
            StartTime = TimeSpan.FromTicks(chapterResponse.StartPositionTicks)
        };

    private static MovieMetadata ProjectToMovieMetadata(JellyfinLibraryItemResponse item)
    {
        DateTime dateAdded = item.DateCreated.UtcDateTime;
        // DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt).DateTime;

        var metadata = new MovieMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = item.Name,
            SortTitle = SortTitle.GetSortTitle(item.Name),
            Plot = item.Overview,
            Year = item.ProductionYear,
            Tagline = Optional(item.Taglines).Flatten().HeadOrNone().IfNone(string.Empty),
            DateAdded = dateAdded,
            ContentRating = item.OfficialRating,
            Genres = Optional(item.Genres).Flatten().Map(g => new Genre { Name = g }).ToList(),
            Tags = Optional(item.Tags).Flatten().Map(t => new Tag { Name = t }).ToList(),
            Studios = Optional(item.Studios).Flatten().Map(s => new Studio { Name = s.Name }).ToList(),
            Actors = Optional(item.People).Flatten().Collect(r => ProjectToActor(r, dateAdded)).ToList(),
            Directors = Optional(item.People).Flatten().Collect(r => ProjectToDirector(r)).ToList(),
            Writers = Optional(item.People).Flatten().Collect(r => ProjectToWriter(r)).ToList(),
            Artwork = new List<Artwork>(),
            Guids = GuidsFromProviderIds(item.ProviderIds),
            Subtitles = new List<Subtitle>()
        };

        // set order on actors
        for (var i = 0; i < metadata.Actors.Count; i++)
        {
            metadata.Actors[i].Order = i;
        }

        if (DateTime.TryParse(item.PremiereDate, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags.Primary))
        {
            var poster = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = $"jellyfin://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(poster);
        }

        if (item.BackdropImageTags.Count != 0)
        {
            var fanArt = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = $"jellyfin://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(fanArt);
        }

        return metadata;
    }

    private static Option<Actor> ProjectToActor(JellyfinPersonResponse person, DateTime dateAdded)
    {
        if (person.Type?.ToLowerInvariant() != "actor")
        {
            return None;
        }

        var actor = new Actor { Name = person.Name, Role = person.Role };
        if (!string.IsNullOrWhiteSpace(person.Id) && !string.IsNullOrWhiteSpace(person.PrimaryImageTag))
        {
            actor.Artwork = new Artwork
            {
                Path = $"jellyfin://Items/{person.Id}/Images/Primary?tag={person.PrimaryImageTag}",
                ArtworkKind = ArtworkKind.Thumbnail,
                DateAdded = dateAdded
            };
        }

        return actor;
    }

    private static Option<Director> ProjectToDirector(JellyfinPersonResponse person)
    {
        if (person.Type?.ToLowerInvariant() != "director")
        {
            return None;
        }

        return new Director { Name = person.Name };
    }

    private static Option<Writer> ProjectToWriter(JellyfinPersonResponse person)
    {
        if (person.Type?.ToLowerInvariant() != "writer")
        {
            return None;
        }

        return new Writer { Name = person.Name };
    }

    private Option<JellyfinShow> ProjectToShow(JellyfinLibraryItemResponse item)
    {
        try
        {
            ShowMetadata metadata = ProjectToShowMetadata(item);

            var show = new JellyfinShow
            {
                ItemId = item.Id,
                Etag = item.Etag,
                ShowMetadata = new List<ShowMetadata> { metadata },
                TraktListItems = new List<TraktListItem>()
            };

            return show;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin show");
            return None;
        }
    }

    private static ShowMetadata ProjectToShowMetadata(JellyfinLibraryItemResponse item)
    {
        DateTime dateAdded = item.DateCreated.UtcDateTime;
        // DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt).DateTime;

        var metadata = new ShowMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = item.Name,
            SortTitle = SortTitle.GetSortTitle(item.Name),
            Plot = item.Overview,
            Year = item.ProductionYear,
            Tagline = Optional(item.Taglines).Flatten().HeadOrNone().IfNone(string.Empty),
            DateAdded = dateAdded,
            ContentRating = item.OfficialRating,
            Genres = Optional(item.Genres).Flatten().Map(g => new Genre { Name = g }).ToList(),
            Tags = Optional(item.Tags).Flatten().Map(t => new Tag { Name = t }).ToList(),
            Studios = Optional(item.Studios).Flatten().Map(s => new Studio { Name = s.Name }).ToList(),
            Actors = Optional(item.People).Flatten().Collect(r => ProjectToActor(r, dateAdded)).ToList(),
            Artwork = new List<Artwork>(),
            Guids = GuidsFromProviderIds(item.ProviderIds)
        };

        // set order on actors
        for (var i = 0; i < metadata.Actors.Count; i++)
        {
            metadata.Actors[i].Order = i;
        }

        if (DateTime.TryParse(item.PremiereDate, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags.Primary))
        {
            var poster = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = $"jellyfin://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(poster);
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags.Thumb))
        {
            var thumb = new Artwork
            {
                ArtworkKind = ArtworkKind.Thumbnail,
                Path = $"jellyfin://Items/{item.Id}/Images/Thumb?tag={item.ImageTags.Thumb}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(thumb);
        }

        if (item.BackdropImageTags.Count != 0)
        {
            var fanArt = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = $"jellyfin://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(fanArt);
        }

        return metadata;
    }

    private Option<JellyfinSeason> ProjectToSeason(JellyfinLibraryItemResponse item)
    {
        try
        {
            DateTime dateAdded = item.DateCreated.UtcDateTime;
            // DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

            var metadata = new SeasonMetadata
            {
                MetadataKind = MetadataKind.External,
                Title = item.Name,
                SortTitle = SortTitle.GetSortTitle(item.Name),
                Year = item.ProductionYear,
                DateAdded = dateAdded,
                Artwork = new List<Artwork>(),
                Guids = GuidsFromProviderIds(item.ProviderIds),
                Tags = new List<Tag>()
            };

            if (!string.IsNullOrWhiteSpace(item.ImageTags.Primary))
            {
                var poster = new Artwork
                {
                    ArtworkKind = ArtworkKind.Poster,
                    Path = $"jellyfin://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                    DateAdded = dateAdded
                };
                metadata.Artwork.Add(poster);
            }

            if (item.BackdropImageTags.Count != 0)
            {
                var fanArt = new Artwork
                {
                    ArtworkKind = ArtworkKind.FanArt,
                    Path = $"jellyfin://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                    DateAdded = dateAdded
                };
                metadata.Artwork.Add(fanArt);
            }

            var season = new JellyfinSeason
            {
                ItemId = item.Id,
                Etag = item.Etag,
                SeasonMetadata = new List<SeasonMetadata> { metadata },
                TraktListItems = new List<TraktListItem>()
            };

            if (item.IndexNumber.HasValue)
            {
                season.SeasonNumber = item.IndexNumber.Value;
            }
            else
            {
                Option<int> maybeSeasonNumber =
                    _fallbackMetadataProvider.GetSeasonNumberForFolder(item.Path ?? string.Empty);

                foreach (int seasonNumber in maybeSeasonNumber)
                {
                    season.SeasonNumber = seasonNumber;
                }
            }

            return season;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin season");
            return None;
        }
    }

    private Option<JellyfinCollection> ProjectToCollection(JellyfinLibraryItemResponse item)
    {
        try
        {
            return new JellyfinCollection
            {
                ItemId = item.Id,
                Etag = item.Etag,
                Name = item.Name
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin collection");
            return None;
        }
    }

    private Option<JellyfinEpisode> ProjectToEpisode(JellyfinLibrary library, JellyfinLibraryItemResponse item)
    {
        try
        {
            if (item.LocationType != "FileSystem")
            {
                return None;
            }

            if (Path.GetExtension(item.Path)?.ToLowerInvariant() == ".strm")
            {
                _logger.LogWarning("STRM files are not supported; skipping {Path}", item.Path);
                return None;
            }

            string path = item.Path ?? string.Empty;
            foreach (JellyfinPathInfo pathInfo in library.PathInfos.Filter(pi =>
                         !string.IsNullOrWhiteSpace(pi.NetworkPath)))
            {
                if (path.StartsWith(pathInfo.NetworkPath, StringComparison.Ordinal))
                {
                    path = _jellyfinPathReplacementService.ReplaceNetworkPath(
                        (JellyfinMediaSource)library.MediaSource,
                        path,
                        pathInfo.NetworkPath,
                        pathInfo.Path);
                }
            }

            var duration = TimeSpan.FromTicks(item.RunTimeTicks);
            var version = new MediaVersion
            {
                Name = "Main",
                Duration = duration,
                DateAdded = item.DateCreated.UtcDateTime,
                MediaFiles =
                [
                    new MediaFile
                    {
                        Path = path,
                        PathHash = PathUtils.GetPathHash(path)
                    }
                ],
                Streams = [],
                Chapters = ProjectToModel(Optional(item.Chapters).Flatten(), duration)
            };

            EpisodeMetadata metadata = ProjectToEpisodeMetadata(item);

            var episode = new JellyfinEpisode
            {
                ItemId = item.Id,
                Etag = item.Etag,
                MediaVersions = [version],
                EpisodeMetadata = [metadata],
                TraktListItems = []
            };

            return episode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Jellyfin episode");
            return None;
        }
    }

    private static EpisodeMetadata ProjectToEpisodeMetadata(JellyfinLibraryItemResponse item)
    {
        DateTime dateAdded = item.DateCreated.UtcDateTime;
        // DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt).DateTime;

        var metadata = new EpisodeMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = item.Name,
            SortTitle = SortTitle.GetSortTitle(item.Name),
            Plot = item.Overview,
            Year = item.ProductionYear,
            DateAdded = dateAdded,
            Genres = Optional(item.Genres).Flatten().Map(g => new Genre { Name = g }).ToList(),
            Tags = Optional(item.Tags).Flatten().Map(t => new Tag { Name = t }).ToList(),
            Studios = new List<Studio>(),
            Actors = Optional(item.People).Flatten().Collect(r => ProjectToActor(r, dateAdded)).ToList(),
            Artwork = new List<Artwork>(),
            Guids = GuidsFromProviderIds(item.ProviderIds),
            Directors = Optional(item.People).Flatten().Collect(r => ProjectToDirector(r)).ToList(),
            Writers = Optional(item.People).Flatten().Collect(r => ProjectToWriter(r)).ToList(),
            Subtitles = new List<Subtitle>()
        };

        if (item.IndexNumber.HasValue)
        {
            metadata.EpisodeNumber = item.IndexNumber.Value;
        }

        if (DateTime.TryParse(item.PremiereDate, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags.Primary))
        {
            var thumbnail = new Artwork
            {
                ArtworkKind = ArtworkKind.Thumbnail,
                Path = $"jellyfin://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(thumbnail);
        }

        return metadata;
    }

    private static List<MetadataGuid> GuidsFromProviderIds(JellyfinProviderIdsResponse providerIds)
    {
        var result = new List<MetadataGuid>();

        if (providerIds != null)
        {
            if (!string.IsNullOrWhiteSpace(providerIds.Imdb))
            {
                result.Add(new MetadataGuid { Guid = $"imdb://{providerIds.Imdb}" });
            }

            if (!string.IsNullOrWhiteSpace(providerIds.Tmdb))
            {
                result.Add(new MetadataGuid { Guid = $"tmdb://{providerIds.Tmdb}" });
            }

            if (!string.IsNullOrWhiteSpace(providerIds.Tvdb))
            {
                result.Add(new MetadataGuid { Guid = $"tvdb://{providerIds.Tvdb}" });
            }
        }

        return result;
    }

    private Option<MediaVersion> ProjectToMediaVersion(JellyfinPlaybackInfoResponse response)
    {
        if (response.MediaSources is null || response.MediaSources.Count == 0)
        {
            _logger.LogWarning("Received empty playback info from Jellyfin");
            return None;
        }

        JellyfinMediaSourceResponse mediaSource = response.MediaSources.Head();

        // jellyfin includes external streams first, obscuring real stream indexes
        // from the source file
        int streamIndexOffset = mediaSource.MediaStreams
            .Filter(s => s.IsExternal)
            .Map(s => s.Index + 1)
            .OrderByDescending(i => i)
            .FirstOrDefault();

        IList<JellyfinMediaStreamResponse> streams = mediaSource.MediaStreams;

        Option<JellyfinMediaStreamResponse> maybeVideoStream =
            streams.Find(s => s.Type == JellyfinMediaStreamType.Video);
        return maybeVideoStream.Map(videoStream =>
        {
            int width = videoStream.Width ?? 1;
            int height = videoStream.Height ?? 1;

            var isAnamorphic = false;
            if (!string.IsNullOrWhiteSpace(videoStream.AspectRatio) && videoStream.AspectRatio.Contains(':'))
            {
                // if width/height != aspect ratio, is anamorphic
                double resolutionRatio = width / (double)height;

                string[] split = videoStream.AspectRatio.Split(":");
                if (double.TryParse(split[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                    double.TryParse(split[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den) &&
                    den != 0)
                {
                    double displayRatio = num / den;

                    isAnamorphic = Math.Abs(resolutionRatio - displayRatio) > 0.01d;
                }
            }

            var version = new MediaVersion
            {
                Duration = TimeSpan.FromTicks(mediaSource.RunTimeTicks),
                SampleAspectRatio = isAnamorphic ? "0:0" : "1:1",
                DisplayAspectRatio = string.IsNullOrWhiteSpace(videoStream.AspectRatio)
                    ? string.Empty
                    : videoStream.AspectRatio,
                VideoScanKind = videoStream.IsInterlaced switch
                {
                    true => VideoScanKind.Interlaced,
                    false => VideoScanKind.Progressive
                },
                Streams = new List<MediaStream>(),
                Width = videoStream.Width ?? 1,
                Height = videoStream.Height ?? 1,
                RFrameRate = videoStream.RealFrameRate.HasValue
                    ? videoStream.RealFrameRate.Value.ToString("0.00###", CultureInfo.InvariantCulture)
                    : string.Empty,
                Chapters = new List<MediaChapter>()
            };

            version.Streams.Add(
                new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Video,
                    Index = videoStream.Index - streamIndexOffset,
                    Codec = videoStream.Codec,
                    Profile = (videoStream.Profile ?? string.Empty).ToLowerInvariant(),
                    Default = videoStream.IsDefault,
                    Language = videoStream.Language,
                    Forced = videoStream.IsForced,
                    PixelFormat = videoStream.PixelFormat,
                    ColorRange = (videoStream.ColorRange ?? string.Empty).ToLowerInvariant(),
                    ColorSpace = (videoStream.ColorSpace ?? string.Empty).ToLowerInvariant(),
                    ColorTransfer = (videoStream.ColorTransfer ?? string.Empty).ToLowerInvariant(),
                    ColorPrimaries = (videoStream.ColorPrimaries ?? string.Empty).ToLowerInvariant()
                });

            foreach (JellyfinMediaStreamResponse audioStream in streams.Filter(s =>
                         s.Type == JellyfinMediaStreamType.Audio))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Audio,
                    Index = audioStream.Index - streamIndexOffset,
                    Codec = audioStream.Codec,
                    Profile = (audioStream.Profile ?? string.Empty).ToLowerInvariant(),
                    Channels = audioStream.Channels ?? 2,
                    Default = audioStream.IsDefault,
                    Forced = audioStream.IsForced,
                    Language = audioStream.Language,
                    Title = audioStream.Title ?? string.Empty
                };

                version.Streams.Add(stream);
            }

            foreach (JellyfinMediaStreamResponse subtitleStream in streams.Filter(s =>
                         s.Type == JellyfinMediaStreamType.Subtitle))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    Title = subtitleStream.Title,
                    Codec = (subtitleStream.Codec ?? string.Empty).ToLowerInvariant(),
                    Default = subtitleStream.IsDefault,
                    Forced = subtitleStream.IsForced,
                    Language = subtitleStream.Language
                };

                if (subtitleStream.IsExternal)
                {
                    stream.MediaStreamKind = MediaStreamKind.ExternalSubtitle;
                    // ensure these don't collide with real indexes from the source file
                    stream.Index = subtitleStream.Index + JellyfinStream.ExternalStreamOffset;
                }
                else
                {
                    stream.MediaStreamKind = MediaStreamKind.Subtitle;
                    stream.Index = subtitleStream.Index - streamIndexOffset;
                }

                version.Streams.Add(stream);
            }

            return version;
        });
    }

    private IJellyfinApi ServiceForAddress(string address)
    {
        var client = _httpClientFactory.CreateClient("RefitCustomClient");
        client.BaseAddress = new Uri(address);
        return RestService.ForGenerated<IJellyfinApi>(client);
    }
}
