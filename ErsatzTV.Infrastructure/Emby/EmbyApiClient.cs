using System.Globalization;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure.Emby.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Refit;

namespace ErsatzTV.Infrastructure.Emby;

public class EmbyApiClient : IEmbyApiClient
{
    private readonly IEmbyPathReplacementService _embyPathReplacementService;
    private readonly ILogger<EmbyApiClient> _logger;
    private readonly IMemoryCache _memoryCache;

    public EmbyApiClient(
        IMemoryCache memoryCache,
        IEmbyPathReplacementService embyPathReplacementService,
        ILogger<EmbyApiClient> logger)
    {
        _memoryCache = memoryCache;
        _embyPathReplacementService = embyPathReplacementService;
        _logger = logger;
    }

    public async Task<Either<BaseError, EmbyServerInformation>> GetServerInformation(
        string address,
        string apiKey)
    {
        try
        {
            IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await service.GetSystemInformation(apiKey, cts.Token)
                .Map(response => new EmbyServerInformation(response.ServerName, response.OperatingSystem));
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogError(ex, "Timeout getting emby server name");
            return BaseError.New("Emby did not respond in time");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting emby server name");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<EmbyLibrary>>> GetLibraries(string address, string apiKey)
    {
        try
        {
            IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
            List<EmbyLibraryResponse> libraries = await service.GetLibraries(apiKey);
            return libraries
                .Map(Project)
                .Somes()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting emby libraries");
            return BaseError.New(ex.Message);
        }
    }

    public IAsyncEnumerable<Tuple<EmbyMovie, int>> GetMovieLibraryItems(
        string address,
        string apiKey,
        EmbyLibrary library)
        => GetPagedLibraryContents(
            address,
            library,
            library.ItemId,
            (service, itemId, skip, pageSize) => service.GetMovieLibraryItems(
                apiKey,
                itemId,
                startIndex: skip,
                limit: pageSize),
            (maybeLibrary, item) => maybeLibrary.Map(lib => ProjectToMovie(lib, item)).Flatten());

    public IAsyncEnumerable<Tuple<EmbyShow, int>> GetShowLibraryItems(
        string address,
        string apiKey,
        EmbyLibrary library)
        => GetPagedLibraryContents(
            address,
            library,
            library.ItemId,
            (service, itemId, skip, pageSize) => service.GetShowLibraryItems(
                apiKey,
                itemId,
                startIndex: skip,
                limit: pageSize),
            (_, item) => ProjectToShow(item));

    public IAsyncEnumerable<Tuple<EmbySeason, int>> GetSeasonLibraryItems(
        string address,
        string apiKey,
        EmbyLibrary library,
        string showId) => GetPagedLibraryContents(
        address,
        library,
        showId,
        (service, itemId, skip, pageSize) => service.GetSeasonLibraryItems(
            apiKey,
            itemId,
            startIndex: skip,
            limit: pageSize),
        (_, item) => ProjectToSeason(item));

    public IAsyncEnumerable<Tuple<EmbyEpisode, int>> GetEpisodeLibraryItems(
        string address,
        string apiKey,
        EmbyLibrary library,
        string showId,
        string seasonId) => GetPagedLibraryContents(
        address,
        library,
        seasonId,
        (service, _, skip, pageSize) => service.GetEpisodeLibraryItems(
            apiKey,
            showId,
            seasonId,
            startIndex: skip,
            limit: pageSize),
        (maybeLibrary, item) => maybeLibrary.Map(lib => ProjectToEpisode(lib, item)).Flatten());

    public IAsyncEnumerable<Tuple<EmbyCollection, int>> GetCollectionLibraryItems(string address, string apiKey)
    {
        // TODO: should we enumerate collection libraries here?

        if (_memoryCache.TryGetValue("emby_collections_library_item_id", out string itemId))
        {
            //_logger.LogDebug("Emby collections library item id is {ItemId}", itemId);

            return GetPagedLibraryContents(
                address,
                None,
                itemId,
                (service, _, skip, pageSize) => service.GetCollectionLibraryItems(
                    apiKey,
                    itemId,
                    startIndex: skip,
                    limit: pageSize),
                (_, item) => ProjectToCollection(item));
        }

        return AsyncEnumerable.Empty<Tuple<EmbyCollection, int>>();
    }

    public IAsyncEnumerable<Tuple<MediaItem, int>> GetCollectionItems(
        string address,
        string apiKey,
        string collectionId) =>
        GetPagedLibraryContents(
            address,
            None,
            collectionId,
            (service, _, skip, pageSize) => service.GetCollectionItems(
                apiKey,
                collectionId,
                startIndex: skip,
                limit: pageSize),
            (_, item) => ProjectToCollectionMediaItem(item));

    public async Task<Either<BaseError, MediaVersion>> GetPlaybackInfo(
        string address,
        string apiKey,
        EmbyLibrary library,
        string itemId)
    {
        try
        {
            IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
            EmbyPlaybackInfoResponse playbackInfo = await service.GetPlaybackInfo(apiKey, itemId);
            Option<MediaVersion> maybeVersion = ProjectToMediaVersion(playbackInfo);
            return maybeVersion.ToEither(() => BaseError.New("Unable to locate Emby statistics"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Emby playback info");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, Option<EmbyShow>>> GetSingleShow(
        string address,
        string apiKey,
        EmbyLibrary library,
        string showId)
    {
        try
        {
            IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
            EmbyLibraryItemsResponse itemsResponse = await service.GetShowLibraryItems(
                apiKey,
                library.ItemId,
                recursive: false,
                startIndex: 0,
                limit: 1,
                ids: showId);

            foreach (EmbyLibraryItemResponse item in itemsResponse.Items)
            {
                return ProjectToShow(item);
            }

            return BaseError.New($"Unable to locate show with id {showId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Emby shows by id");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<EmbyShow>>> SearchShowsByTitle(
        string address,
        string apiKey,
        EmbyLibrary library,
        string showTitle)
    {
        try
        {
            IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
            EmbySearchHintsResponse searchResponse = await service.SearchHints(
                apiKey,
                showTitle,
                "Series",
                library.ItemId);

            var shows = new List<EmbyShow>();

            foreach (EmbySearchHintResponse hint in searchResponse.SearchHints)
            {
                if (hint.Type == "Series" &&
                    string.Equals(hint.Name, showTitle, StringComparison.OrdinalIgnoreCase))
                {
                    EmbyLibraryItemsResponse detailResponse = await service.GetShowLibraryItems(
                        apiKey,
                        hint.Id,
                        recursive: false,
                        startIndex: 0,
                        limit: 1);

                    foreach (EmbyLibraryItemResponse item in detailResponse.Items)
                    {
                        Option<EmbyShow> maybeShow = ProjectToShow(item);
                        foreach (EmbyShow show in maybeShow)
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
            _logger.LogError(ex, "Error searching Emby shows by title");
            return BaseError.New(ex.Message);
        }
    }

    private static async IAsyncEnumerable<Tuple<TItem, int>> GetPagedLibraryContents<TItem>(
        string address,
        Option<EmbyLibrary> maybeLibrary,
        string parentId,
        Func<IEmbyApi, string, int, int, Task<EmbyLibraryItemsResponse>> getItems,
        Func<Option<EmbyLibrary>, EmbyLibraryItemResponse, Option<TItem>> mapper)
    {
        IEmbyApi service = RestService.ForGenerated<IEmbyApi>(address);
        const int PAGE_SIZE = 10;

        int pages = int.MaxValue;
        for (var i = 0; i < pages; i++)
        {
            int skip = i * PAGE_SIZE;

            EmbyLibraryItemsResponse result = await getItems(service, parentId, skip, PAGE_SIZE);

            // update page count
            pages = Math.Min(pages, (result.TotalRecordCount - 1) / PAGE_SIZE + 1);

#pragma warning disable VSTHRD003
            foreach (TItem item in result.Items.Map(item => mapper(maybeLibrary, item)).Somes())
#pragma warning restore VSTHRD003
            {
                yield return new Tuple<TItem, int>(item, result.TotalRecordCount);
            }
        }
    }

    private Option<EmbyCollection> ProjectToCollection(EmbyLibraryItemResponse item)
    {
        try
        {
            return new EmbyCollection
            {
                ItemId = item.Id,
                Etag = item.Etag,
                Name = item.Name
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Emby collection");
            return None;
        }
    }

    private Option<MediaItem> ProjectToCollectionMediaItem(EmbyLibraryItemResponse item)
    {
        try
        {
            return item.Type switch
            {
                "Movie" => new EmbyMovie { ItemId = item.Id },
                "Series" => new EmbyShow { ItemId = item.Id },
                "Season" => new EmbySeason { ItemId = item.Id },
                "Episode" => new EmbyEpisode { ItemId = item.Id },
                _ => Option<MediaItem>.None
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Emby collection media item");
            return None;
        }
    }

    private Option<EmbyLibrary> Project(EmbyLibraryResponse response) =>
        response.CollectionType?.ToLowerInvariant() switch
        {
            "tvshows" => new EmbyLibrary
            {
                ItemId = response.ItemId,
                Name = response.Name,
                MediaKind = LibraryMediaKind.Shows,
                ShouldSyncItems = false,
                Paths = new List<LibraryPath> { new() { Path = $"emby://{response.ItemId}" } },
                PathInfos = GetPathInfos(response)
            },
            "movies" => new EmbyLibrary
            {
                ItemId = response.ItemId,
                Name = response.Name,
                MediaKind = LibraryMediaKind.Movies,
                ShouldSyncItems = false,
                Paths = new List<LibraryPath> { new() { Path = $"emby://{response.ItemId}" } },
                PathInfos = GetPathInfos(response)
            },
            // TODO: ??? for music libraries
            "boxsets" => CacheCollectionLibraryId(response.ItemId),
            _ => None
        };

    private static List<EmbyPathInfo> GetPathInfos(EmbyLibraryResponse response) =>
        response.LibraryOptions.PathInfos
            .Filter(pi => !string.IsNullOrWhiteSpace(pi.NetworkPath))
            .Map(pi => new EmbyPathInfo
            {
                Path = pi.Path,
                NetworkPath = pi.NetworkPath
            }).ToList();

    private Option<EmbyLibrary> CacheCollectionLibraryId(string itemId)
    {
        _memoryCache.Set("emby_collections_library_item_id", itemId);
        return None;
    }

    private Option<EmbyMovie> ProjectToMovie(EmbyLibrary library, EmbyLibraryItemResponse item)
    {
        try
        {
            if (item.MediaSources is null || item.MediaSources.Count == 0)
            {
                return None;
            }

            string path = item.Path ?? string.Empty;
            foreach (EmbyPathInfo pathInfo in
                     library.PathInfos.Filter(pi => !string.IsNullOrWhiteSpace(pi.NetworkPath)))
            {
                if (path.StartsWith(pathInfo.NetworkPath, StringComparison.Ordinal))
                {
                    path = _embyPathReplacementService.ReplaceNetworkPath(
                        (EmbyMediaSource)library.MediaSource,
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

            var movie = new EmbyMovie
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
            _logger.LogWarning(ex, "Error projecting Emby movie");
            return None;
        }
    }

    private static List<MediaChapter> ProjectToModel(
        IEnumerable<EmbyChapterResponse> embyChapters,
        TimeSpan duration)
    {
        var models = embyChapters.Map(ProjectToModel).OrderBy(c => c.StartTime).ToList();

        for (var index = 0; index < models.Count; index++)
        {
            MediaChapter model = models[index];
            model.ChapterId = index;
            model.EndTime = index == models.Count - 1 ? duration : models[index + 1].StartTime;
        }

        return models;
    }

    private static MediaChapter ProjectToModel(EmbyChapterResponse chapterResponse) =>
        new()
        {
            Title = chapterResponse.Name,
            StartTime = TimeSpan.FromTicks(chapterResponse.StartPositionTicks)
        };

    private static MovieMetadata ProjectToMovieMetadata(EmbyLibraryItemResponse item)
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
            Tags = Optional(item.TagItems).Flatten().Map(t => new Tag { Name = t.Name }).ToList(),
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
                Path = $"emby://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(poster);
        }

        if (item.BackdropImageTags.Count != 0)
        {
            var fanArt = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = $"emby://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(fanArt);
        }

        return metadata;
    }

    private static Option<Actor> ProjectToActor(EmbyPersonResponse person, DateTime dateAdded)
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
                Path = $"emby://Items/{person.Id}/Images/Primary?tag={person.PrimaryImageTag}",
                ArtworkKind = ArtworkKind.Thumbnail,
                DateAdded = dateAdded
            };
        }

        return actor;
    }

    private static Option<Director> ProjectToDirector(EmbyPersonResponse person)
    {
        if (person.Type?.ToLowerInvariant() != "director")
        {
            return None;
        }

        return new Director { Name = person.Name };
    }

    private static Option<Writer> ProjectToWriter(EmbyPersonResponse person)
    {
        if (person.Type?.ToLowerInvariant() != "writer")
        {
            return None;
        }

        return new Writer { Name = person.Name };
    }

    private Option<EmbyShow> ProjectToShow(EmbyLibraryItemResponse item)
    {
        try
        {
            ShowMetadata metadata = ProjectToShowMetadata(item);

            var show = new EmbyShow
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
            _logger.LogWarning(ex, "Error projecting Emby show");
            return None;
        }
    }

    private static ShowMetadata ProjectToShowMetadata(EmbyLibraryItemResponse item)
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
            Tags = Optional(item.TagItems).Flatten().Map(t => new Tag { Name = t.Name }).ToList(),
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
                Path = $"emby://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(poster);
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags.Thumb))
        {
            var thumb = new Artwork
            {
                ArtworkKind = ArtworkKind.Thumbnail,
                Path = $"emby://Items/{item.Id}/Images/Thumb?tag={item.ImageTags.Thumb}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(thumb);
        }

        if (item.BackdropImageTags.Count != 0)
        {
            var fanArt = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = $"emby://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(fanArt);
        }

        return metadata;
    }

    private Option<EmbySeason> ProjectToSeason(EmbyLibraryItemResponse item)
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
                    Path = $"emby://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                    DateAdded = dateAdded
                };
                metadata.Artwork.Add(poster);
            }

            if (item.BackdropImageTags.Count != 0)
            {
                var fanArt = new Artwork
                {
                    ArtworkKind = ArtworkKind.FanArt,
                    Path = $"emby://Items/{item.Id}/Images/Backdrop?tag={item.BackdropImageTags.Head()}",
                    DateAdded = dateAdded
                };
                metadata.Artwork.Add(fanArt);
            }

            var season = new EmbySeason
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

            return season;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting Emby show");
            return None;
        }
    }

    private Option<EmbyEpisode> ProjectToEpisode(EmbyLibrary library, EmbyLibraryItemResponse item)
    {
        try
        {
            if (item.LocationType == "Virtual")
            {
                return None;
            }

            string path = item.Path ?? string.Empty;
            foreach (EmbyPathInfo pathInfo in
                     library.PathInfos.Filter(pi => !string.IsNullOrWhiteSpace(pi.NetworkPath)))
            {
                if (path.StartsWith(pathInfo.NetworkPath, StringComparison.Ordinal))
                {
                    path = _embyPathReplacementService.ReplaceNetworkPath(
                        (EmbyMediaSource)library.MediaSource,
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

            var episode = new EmbyEpisode
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
            _logger.LogWarning(ex, "Error projecting Emby movie");
            return None;
        }
    }

    private static EpisodeMetadata ProjectToEpisodeMetadata(EmbyLibraryItemResponse item)
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
            Tags = Optional(item.TagItems).Flatten().Map(t => new Tag { Name = t.Name }).ToList(),
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
                Path = $"emby://Items/{item.Id}/Images/Primary?tag={item.ImageTags.Primary}",
                DateAdded = dateAdded
            };
            metadata.Artwork.Add(thumbnail);
        }

        return metadata;
    }

    private static List<MetadataGuid> GuidsFromProviderIds(EmbyProviderIdsResponse providerIds)
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

    private Option<MediaVersion> ProjectToMediaVersion(EmbyPlaybackInfoResponse response)
    {
        if (response.MediaSources is null || response.MediaSources.Count == 0)
        {
            _logger.LogWarning("Received empty playback info from Jellyfin");
            return None;
        }

        EmbyMediaSourceResponse mediaSource = response.MediaSources.Head();
        IList<EmbyMediaStreamResponse> streams = mediaSource.MediaStreams;
        Option<EmbyMediaStreamResponse> maybeVideoStream =
            streams.Find(s => s.Type == EmbyMediaStreamType.Video);
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
                var num = double.Parse(split[0], CultureInfo.InvariantCulture);
                var den = double.Parse(split[1], CultureInfo.InvariantCulture);
                double aspectRatio = num / den;

                isAnamorphic = Math.Abs(resolutionRatio - aspectRatio) > 0.01d;
            }
            else if (videoStream.IsAnamorphic.HasValue)
            {
                isAnamorphic = videoStream.IsAnamorphic.Value;
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
                    Index = videoStream.Index,
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

            foreach (EmbyMediaStreamResponse audioStream in streams.Filter(s => s.Type == EmbyMediaStreamType.Audio))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Audio,
                    Index = audioStream.Index,
                    Codec = audioStream.Codec,
                    Profile = (audioStream.Profile ?? string.Empty).ToLowerInvariant(),
                    Channels = audioStream.Channels ?? 2,
                    Default = audioStream.IsDefault,
                    Forced = audioStream.IsForced,
                    Language = audioStream.Language,
                    Title = audioStream.DisplayTitle ?? string.Empty
                };

                version.Streams.Add(stream);
            }

            foreach (EmbyMediaStreamResponse subtitleStream in streams.Filter(s =>
                         s.Type == EmbyMediaStreamType.Subtitle))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = subtitleStream.IsExternal == true
                        ? MediaStreamKind.ExternalSubtitle
                        : MediaStreamKind.Subtitle,
                    Index = subtitleStream.Index,
                    Codec = (subtitleStream.Codec ?? string.Empty).ToLowerInvariant(),
                    Default = subtitleStream.IsDefault,
                    Forced = subtitleStream.IsForced,
                    Language = subtitleStream.Language
                };

                // hacky, oh well
                if (subtitleStream.IsExternal == true)
                {
                    stream.FileName = mediaSource.Id;
                }

                version.Streams.Add(stream);
            }

            return version;
        });
    }
}
