using System.Collections.Specialized;
using System.Globalization;
using System.Web;
using System.Xml.Serialization;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Plex;
using ErsatzTV.Infrastructure.Plex.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Refit;

namespace ErsatzTV.Infrastructure.Plex;

public class PlexServerApiClient(PlexEtag plexEtag, ILogger<PlexServerApiClient> logger) : IPlexServerApiClient
{
    private const string PersonalMediaAgent = "com.plexapp.agents.none";
    private const string PlexPersonalMediaAgent = "tv.plex.agents.none";

    public async Task<bool> Ping(
        PlexConnection connection,
        PlexServerAuthToken token,
        CancellationToken cancellationToken)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri, TimeSpan.FromSeconds(5));
            PlexXmlMediaContainerPingResponse pingResult = await service.Ping(token.AuthToken, cancellationToken);
            return token.ClientIdentifier == pingResult.MachineIdentifier;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<Either<BaseError, List<PlexLibrary>>> GetLibraries(
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = RestService.For<IPlexServerApi>(
                new HttpClient
                {
                    BaseAddress = new Uri(connection.Uri),
                    Timeout = TimeSpan.FromSeconds(10)
                });
            List<PlexLibraryResponse> directory =
                await service.GetLibraries(token.AuthToken).Map(r => r.MediaContainer.Directory);
            var response = directory
                .Filter(l => l.Type.ToLowerInvariant() is "movie" or "show")
                .Map(Project)
                .Somes()
                .ToList();
            return response;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public IAsyncEnumerable<Tuple<PlexMovie, int>> GetMovieLibraryContents(
        PlexLibrary library,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetLibrarySection(library.Key, token.AuthToken);
        }

        Task<IEnumerable<PlexMovie>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetLibrarySectionContents(library.Key, skip, pageSize, token.AuthToken)
                .Map(r => r.MediaContainer.Metadata.Filter(m =>
                    m.Media.Count > 0 && m.Media.Any(media => media.Part.Count > 0)))
                .Map(list => list.Map(metadata => ProjectToMovie(metadata, library.MediaSourceId)));
        }

        return GetPagedLibraryContents(connection, CountItems, GetItems);
    }

    public IAsyncEnumerable<Tuple<PlexShow, int>> GetShowLibraryContents(
        PlexLibrary library,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetLibrarySection(library.Key, token.AuthToken);
        }

        Task<IEnumerable<PlexShow>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetLibrarySectionContents(library.Key, skip, pageSize, token.AuthToken)
                .Map(r => r.MediaContainer.Metadata ?? new List<PlexMetadataResponse>())
                .Map(list => list.Map(metadata => ProjectToShow(metadata, library.MediaSourceId)));
        }

        return GetPagedLibraryContents(connection, CountItems, GetItems);
    }

    public IAsyncEnumerable<Tuple<PlexOtherVideo, int>> GetOtherVideoLibraryContents(
        PlexLibrary library,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetLibrarySection(library.Key, token.AuthToken);
        }

        Task<IEnumerable<PlexOtherVideo>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetLibrarySectionContents(library.Key, skip, pageSize, token.AuthToken)
                .Map(r => r.MediaContainer.Metadata.Filter(m =>
                    m.Media.Count > 0 && m.Media.Any(media => media.Part.Count > 0)))
                .Map(list => list.Map(metadata => ProjectToOtherVideo(metadata, library.MediaSourceId, library)));
        }

        return GetPagedLibraryContents(connection, CountItems, GetItems);
    }

    public IAsyncEnumerable<Tuple<PlexSeason, int>> GetShowSeasons(
        PlexLibrary library,
        PlexShow show,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        string showMetadataKey = show.Key.Split("/").Reverse().Skip(1).Head();

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.CountShowChildren(showMetadataKey, token.AuthToken);
        }

        Task<IEnumerable<PlexSeason>> GetItems(IPlexServerApi xmlService, IPlexServerApi _, int skip, int pageSize)
        {
            return xmlService.GetShowChildren(showMetadataKey, skip, pageSize, token.AuthToken)
                .Map(r => r.Metadata.Filter(m => !m.Key.Contains("allLeaves")))
                .Map(list => list.Map(metadata => ProjectToSeason(metadata, library.MediaSourceId)));
        }

        return GetPagedLibraryContents(connection, CountItems, GetItems);
    }

    public IAsyncEnumerable<Tuple<PlexEpisode, int>> GetSeasonEpisodes(
        PlexLibrary library,
        PlexSeason season,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        string seasonMetadataKey = season.Key.Split("/").Reverse().Skip(1).Head();

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.CountSeasonChildren(seasonMetadataKey, token.AuthToken);
        }

        Task<IEnumerable<PlexEpisode>> GetItems(IPlexServerApi xmlService, IPlexServerApi _, int skip, int pageSize)
        {
            return xmlService.GetSeasonChildren(seasonMetadataKey, skip, pageSize, token.AuthToken)
                .Map(r => r.Metadata.Filter(m => m.Media.Count > 0 && m.Media.Any(media => media.Part.Count > 0)))
                .Map(list => list.Map(metadata => ProjectToEpisode(metadata, library.MediaSourceId)));
        }

        return GetPagedLibraryContents(connection, CountItems, GetItems);
    }

    public async Task<Either<BaseError, ShowMetadata>> GetShowMetadata(
        PlexLibrary library,
        string key,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri);
            return await service.GetDirectoryMetadata(key, token.AuthToken)
                .Map(Optional)
                .MapT(response => ProjectToShowMetadata(response.Metadata, library.MediaSourceId))
                .Map(o => o.ToEither<BaseError>("Unable to locate metadata"));
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public async Task<Either<BaseError, Tuple<MovieMetadata, MediaVersion>>> GetMovieMetadataAndStatistics(
        int plexMediaSourceId,
        string key,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri);
            Option<PlexXmlVideoMetadataResponseContainer> maybeResponse = await service
                .GetVideoMetadata(key, token.AuthToken)
                .Map(Optional)
                .Map(r => r.Filter(m =>
                    m.Metadata.Media.Count > 0 && m.Metadata.Media.Any(media => media.Part.Count > 0)));
            return maybeResponse.Match(
                response =>
                {
                    Option<MediaVersion> maybeVersion = ProjectToMediaVersion(response.Metadata);
                    return maybeVersion.Match<Either<BaseError, Tuple<MovieMetadata, MediaVersion>>>(
                        version => Tuple(
                            ProjectToMovieMetadata(version, response.Metadata, plexMediaSourceId),
                            version),
                        () => BaseError.New("Unable to locate metadata"));
                },
                () => BaseError.New("Unable to locate metadata"));
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public async Task<Either<BaseError, Tuple<OtherVideoMetadata, MediaVersion>>> GetOtherVideoMetadataAndStatistics(
        int plexMediaSourceId,
        string key,
        PlexConnection connection,
        PlexServerAuthToken token,
        PlexLibrary library)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri);
            Option<PlexXmlVideoMetadataResponseContainer> maybeResponse = await service
                .GetVideoMetadata(key, token.AuthToken)
                .Map(Optional)
                .Map(r => r.Filter(m =>
                    m.Metadata.Media.Count > 0 && m.Metadata.Media.Any(media => media.Part.Count > 0)));
            return maybeResponse.Match(
                response =>
                {
                    Option<MediaVersion> maybeVersion = ProjectToMediaVersion(response.Metadata);
                    return maybeVersion.Match<Either<BaseError, Tuple<OtherVideoMetadata, MediaVersion>>>(
                        version => Tuple(
                            ProjectToOtherVideoMetadata(version, response.Metadata, plexMediaSourceId, library),
                            version),
                        () => BaseError.New("Unable to locate metadata"));
                },
                () => BaseError.New("Unable to locate metadata"));
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public async Task<Either<BaseError, Tuple<EpisodeMetadata, MediaVersion>>> GetEpisodeMetadataAndStatistics(
        int plexMediaSourceId,
        string key,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri);
            Option<PlexXmlVideoMetadataResponseContainer> maybeResponse = await service
                .GetVideoMetadata(key, token.AuthToken)
                .Map(Optional)
                .Map(r => r.Filter(m =>
                    m.Metadata.Media.Count > 0 && m.Metadata.Media.Any(media => media.Part.Count > 0)));
            return maybeResponse.Match(
                response =>
                {
                    Option<MediaVersion> maybeVersion = ProjectToMediaVersion(response.Metadata);
                    return maybeVersion.Match<Either<BaseError, Tuple<EpisodeMetadata, MediaVersion>>>(
                        version => Tuple(
                            ProjectToEpisodeMetadata(version, response.Metadata, plexMediaSourceId),
                            version),
                        () => BaseError.New("Unable to locate metadata"));
                },
                () => BaseError.New("Unable to locate metadata"));
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public IAsyncEnumerable<Tuple<PlexCollection, int>> GetAllCollections(
        PlexConnection connection,
        PlexServerAuthToken token,
        CancellationToken cancellationToken)
    {
        return GetPagedLibraryContents(connection, CountItems, GetItems);

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetCollectionCount(token.AuthToken);
        }

        Task<IEnumerable<PlexCollection>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetCollections(skip, pageSize, token.AuthToken)
                .Map(r => r.MediaContainer.Metadata)
                .Map(list => list.Map(m => ProjectToCollection(connection.PlexMediaSource, m)).Somes());
        }
    }

    public IAsyncEnumerable<Tuple<MediaItem, int>> GetCollectionItems(
        PlexConnection connection,
        PlexServerAuthToken token,
        string key,
        CancellationToken cancellationToken)
    {
        return GetPagedLibraryContents(connection, CountItems, GetItems);

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetCollectionItemsCount(key, token.AuthToken);
        }

        Task<IEnumerable<MediaItem>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetCollectionItems(key, skip, pageSize, token.AuthToken)
                .Map(r => Optional(r.MediaContainer.Metadata).Flatten())
                .Map(list => list.Map(ProjectToCollectionMediaItem).Somes());
        }
    }

    public IAsyncEnumerable<Tuple<PlexTag, int>> GetAllTags(
        PlexConnection connection,
        PlexServerAuthToken token,
        int tagType,
        CancellationToken cancellationToken)
    {
        return GetPagedLibraryContents(connection, CountItems, GetItems);

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.GetTagsCount(tagType, token.AuthToken);
        }

        Task<IEnumerable<PlexTag>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetTags(tagType, skip, pageSize, token.AuthToken)
                .Map(r => r.MediaContainer.Directory)
                .Map(list => list.Map(ProjectToTag).Somes());
        }
    }

    public IAsyncEnumerable<Tuple<PlexShow, int>> GetTagShowContents(
        PlexLibrary library,
        PlexConnection connection,
        PlexServerAuthToken token,
        PlexTag tag)
    {
        string network = string.Empty;

        NameValueCollection parsedFilter = HttpUtility.ParseQueryString(tag.Filter);
        foreach (string key in parsedFilter.AllKeys.Filter(k => k is not null))
        {
            network = parsedFilter[key];
        }

        var filter = new NetworkFilter { Network = network };

        return GetPagedLibraryContents(connection, CountItems, GetItems);

        Task<PlexXmlMediaContainerStatsResponse> CountItems(IPlexServerApi service)
        {
            return service.CountTagContents(library.Key, token.AuthToken, filter);
        }

        Task<IEnumerable<PlexShow>> GetItems(IPlexServerApi _, IPlexServerApi jsonService, int skip, int pageSize)
        {
            return jsonService
                .GetTagContents(library.Key, skip, pageSize, token.AuthToken, filter)
                .Map(r => r.MediaContainer.Metadata ?? [])
                .Map(list => list.Map(metadata => ProjectToShow(metadata, library.MediaSourceId)));
        }
    }

    public async Task<Either<BaseError, Option<PlexShow>>> GetSingleShow(
        PlexLibrary library,
        string showKey,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = XmlServiceFor(connection.Uri);
            return await service.GetDirectoryMetadata(showKey, token.AuthToken)
                .Map(Optional)
                .MapT(response => Some(ProjectToShow(response.Metadata, library.MediaSourceId)))
                .Map(o => o.ToEither<BaseError>($"Unable to locate show with key {showKey}"));
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public async Task<Either<BaseError, List<PlexShow>>> SearchShowsByTitle(
        PlexLibrary library,
        string showTitle,
        PlexConnection connection,
        PlexServerAuthToken token)
    {
        try
        {
            IPlexServerApi service = RestService.For<IPlexServerApi>(
                new HttpClient { BaseAddress = new Uri(connection.Uri) });

            PlexMediaContainerResponse<PlexMediaContainerHubContent<PlexHubResponse>> searchResponse =
                await service.Search(showTitle, library.Key, token.AuthToken);

            var shows = new List<PlexShow>();

            foreach (PlexHubResponse hub in searchResponse.MediaContainer.Hub)
            {
                if (hub.Type != "show")
                {
                    continue;
                }

                var fullKey = $"/library/sections/{library.Key}";

                foreach (PlexMetadataResponse metadata in hub.Metadata.Where(m => m.LibrarySectionKey == fullKey))
                {
                    if (string.Equals(metadata.Title, showTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        PlexShow show = ProjectToShow(metadata, library.MediaSourceId);
                        shows.Add(show);
                    }
                }
            }

            return shows;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private static async IAsyncEnumerable<Tuple<TItem, int>> GetPagedLibraryContents<TItem>(
        PlexConnection connection,
        Func<IPlexServerApi, Task<PlexXmlMediaContainerStatsResponse>> countItems,
        Func<IPlexServerApi, IPlexServerApi, int, int, Task<IEnumerable<TItem>>> getItems)
    {
        IPlexServerApi xmlService = XmlServiceFor(connection.Uri);

        int size = await countItems(xmlService).Map(r => r.TotalSize);
        if (size == 0)
        {
            yield break;
        }

        const int PAGE_SIZE = 10;

        IPlexServerApi jsonService = RestService.For<IPlexServerApi>(connection.Uri);
        int pages = (size - 1) / PAGE_SIZE + 1;

        for (var i = 0; i < pages; i++)
        {
            int skip = i * PAGE_SIZE;

            Task<IEnumerable<TItem>> result = getItems(xmlService, jsonService, skip, PAGE_SIZE);

            foreach (TItem item in await result)
            {
                yield return new Tuple<TItem, int>(item, size);
            }
        }
    }

    private static IPlexServerApi XmlServiceFor(string uri, TimeSpan? timeout = null)
    {
        var overrides = new XmlAttributeOverrides();
        var attrs = new XmlAttributes { XmlIgnore = true };
        overrides.Add(typeof(PlexMetadataResponse), "Media", attrs);

        TimeSpan httpClientTimeout = timeout ?? TimeSpan.FromSeconds(30);

        return RestService.For<IPlexServerApi>(
            new HttpClient
            {
                BaseAddress = new Uri(uri),
                Timeout = httpClientTimeout
            },
            new RefitSettings
            {
                ContentSerializer = new XmlContentSerializer(
                    new XmlContentSerializerSettings
                    {
                        XmlAttributeOverrides = overrides
                    })
            });
    }

    private static Option<PlexLibrary> Project(PlexLibraryResponse response)
    {
        List<LibraryPath> paths =
        [
            new()
            {
                Path = JsonConvert.SerializeObject(
                    new LibraryPaths { Paths = response.Location.Map(l => l.Path).ToList() })
            }
        ];

        return response.Type switch
        {
            "show" => new PlexLibrary
            {
                Key = response.Key,
                Name = response.Title,
                MediaKind = LibraryMediaKind.Shows,
                ShouldSyncItems = false,
                Paths = paths
            },
            "movie" => new PlexLibrary
            {
                Key = response.Key,
                Name = response.Title,
                MediaKind = response.Agent is PersonalMediaAgent or PlexPersonalMediaAgent
                    ? LibraryMediaKind.OtherVideos
                    : LibraryMediaKind.Movies,
                ShouldSyncItems = false,
                Paths = paths
            },
            // TODO: "artist" for music libraries
            _ => None
        };
    }

    private Option<PlexCollection> ProjectToCollection(
        PlexMediaSource plexMediaSource,
        PlexCollectionMetadataResponse item)
    {
        try
        {
            // skip collections in libraries that are not synchronized
            if (plexMediaSource.Libraries.OfType<PlexLibrary>().Any(l =>
                    l.Key == item.LibrarySectionId.ToString(CultureInfo.InvariantCulture) &&
                    !l.ShouldSyncItems))
            {
                return Option<PlexCollection>.None;
            }

            return new PlexCollection
            {
                Key = item.RatingKey,
                Etag = plexEtag.ForCollection(item),
                Name = item.Title
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error projecting Plex collection");
            return None;
        }
    }

    private Option<MediaItem> ProjectToCollectionMediaItem(PlexCollectionItemMetadataResponse item)
    {
        try
        {
            return item.Type switch
            {
                "movie" => new PlexMovie { Key = item.Key },
                "show" => new PlexShow { Key = item.Key },
                "season" => new PlexSeason { Key = item.Key },
                "episode" => new PlexEpisode { Key = item.Key },
                _ => None
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error projecting Plex collection media item");
            return None;
        }
    }

    private Option<PlexTag> ProjectToTag(PlexTagMetadataResponse item)
    {
        try
        {
            return new PlexTag
            {
                Id = item.Id,
                Filter = item.Filter,
                Tag = item.Tag,
                TagType = item.TagType
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error projecting Plex tag");
            return None;
        }
    }

    private PlexMovie ProjectToMovie(PlexMetadataResponse response, int mediaSourceId)
    {
        PlexMediaResponse<PlexPartResponse> media = response.Media
            .Filter(media => media.Part.Count != 0)
            .MaxBy(media => media.Id);

        PlexPartResponse part = media.Part.Head();
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var version = new MediaVersion
        {
            Name = "Main",
            Duration = TimeSpan.FromMilliseconds(media.Duration),
            Width = media.Width,
            Height = media.Height,
            // specifically omit sample aspect ratio
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            MediaFiles =
            [
                new PlexMediaFile
                {
                    PlexId = part.Id,
                    Key = part.Key,
                    Path = part.File,
                    PathHash = PathUtils.GetPathHash(part.File)
                }
            ],
            Streams = []
        };

        MovieMetadata metadata = ProjectToMovieMetadata(version, response, mediaSourceId);

        var movie = new PlexMovie
        {
            Etag = plexEtag.ForMovie(response),
            Key = response.Key,
            MovieMetadata = [metadata],
            MediaVersions = [version],
            TraktListItems = []
        };

        return movie;
    }

    private MovieMetadata ProjectToMovieMetadata(MediaVersion version, PlexMetadataResponse response, int mediaSourceId)
    {
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var metadata = new MovieMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = response.Title,
            SortTitle = SortTitle.GetSortTitle(response.Title),
            Plot = response.Summary,
            Year = response.Year,
            Tagline = response.Tagline,
            ContentRating = response.ContentRating,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            Genres = Optional(response.Genre).Flatten().Map(g => new Genre { Name = g.Tag }).ToList(),
            Tags = new List<Tag>(),
            Studios = new List<Studio>(),
            Actors = Optional(response.Role).Flatten().Map(r => ProjectToModel(r, dateAdded, lastWriteTime))
                .ToList(),
            Directors = Optional(response.Director).Flatten().Map(d => new Director { Name = d.Tag }).ToList(),
            Writers = Optional(response.Writer).Flatten().Map(w => new Writer { Name = w.Tag }).ToList(),
            Subtitles = new List<Subtitle>()
        };

        var subtitleStreams = version.Streams
            .Filter(s => s.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
            .ToList();

        metadata.Subtitles.AddRange(subtitleStreams.Map(Subtitle.FromMediaStream));

        if (response is PlexXmlMetadataResponse xml)
        {
            metadata.Guids = Optional(xml.Guid).Flatten().Map(g => new MetadataGuid { Guid = g.Id }).ToList();
            if (!string.IsNullOrWhiteSpace(xml.PlexGuid))
            {
                Option<string> normalized = NormalizeGuid(xml.PlexGuid);
                foreach (string guid in normalized)
                {
                    if (metadata.Guids.All(g => g.Guid != guid))
                    {
                        metadata.Guids.Add(new MetadataGuid { Guid = guid });
                    }
                }
            }
        }
        else
        {
            metadata.Guids = new List<MetadataGuid>();
        }

        foreach (PlexLabelResponse label in Optional(response.Label).Flatten())
        {
            metadata.Tags.Add(
                new Tag { Name = label.Tag, ExternalCollectionId = label.Id.ToString(CultureInfo.InvariantCulture) });
        }

        if (!string.IsNullOrWhiteSpace(response.Studio))
        {
            metadata.Studios.Add(new Studio { Name = response.Studio });
        }

        if (DateTime.TryParse(response.OriginallyAvailableAt, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            var path = $"plex/{mediaSourceId}{response.Thumb}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        if (!string.IsNullOrWhiteSpace(response.Art))
        {
            var path = $"plex/{mediaSourceId}{response.Art}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        return metadata;
    }

    private static Option<MediaVersion> ProjectToMediaVersion(PlexXmlMetadataResponse response)
    {
        PlexMediaResponse<PlexXmlPartResponse> media = response.Media
            .Filter(media => media.Part.Count != 0)
            .MaxBy(media => media.Id);

        List<PlexStreamResponse> streams = media.Part.Head().Stream;
        DateTime dateUpdated = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;
        Option<PlexStreamResponse> maybeVideoStream = streams.Find(s => s.StreamType == 1);
        return maybeVideoStream.Map(videoStream =>
        {
            var version = new MediaVersion
            {
                Duration = TimeSpan.FromMilliseconds(media.Duration),
                SampleAspectRatio = string.IsNullOrWhiteSpace(videoStream.PixelAspectRatio)
                    ? "1:1"
                    : videoStream.PixelAspectRatio,
                VideoScanKind = videoStream.ScanType switch
                {
                    "interlaced" => VideoScanKind.Interlaced,
                    "progressive" => VideoScanKind.Progressive,
                    _ => VideoScanKind.Unknown
                },
                Streams = new List<MediaStream>(),
                DateUpdated = dateUpdated,
                Width = videoStream.Width,
                Height = videoStream.Height,
                RFrameRate = videoStream.FrameRate,
                DisplayAspectRatio = media.AspectRatio == 0
                    ? string.Empty
                    : media.AspectRatio.ToString("0.00###", CultureInfo.InvariantCulture),
                Chapters = Optional(response.Chapters).Flatten().Map(ProjectToModel).ToList()
            };

            version.Streams.Add(
                new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Video,
                    Index = videoStream.Index!.Value,
                    Codec = videoStream.Codec,
                    Profile = (videoStream.Profile ?? string.Empty).ToLowerInvariant(),
                    Default = videoStream.Default,
                    Language = videoStream.LanguageCode,
                    Forced = videoStream.Forced,
                    BitsPerRawSample = videoStream.BitDepth,
                    ColorRange = (videoStream.ColorRange ?? string.Empty).ToLowerInvariant(),
                    ColorSpace = (videoStream.ColorSpace ?? string.Empty).ToLowerInvariant(),
                    ColorTransfer = (videoStream.ColorTrc ?? string.Empty).ToLowerInvariant(),
                    ColorPrimaries = (videoStream.ColorPrimaries ?? string.Empty).ToLowerInvariant()
                });

            foreach (PlexStreamResponse audioStream in streams.Filter(s => s.StreamType == 2 && s.Index.HasValue))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Audio,
                    Index = audioStream.Index.Value,
                    Codec = audioStream.Codec,
                    Profile = (audioStream.Profile ?? string.Empty).ToLowerInvariant(),
                    Channels = audioStream.Channels,
                    Default = audioStream.Default,
                    Forced = audioStream.Forced,
                    Language = audioStream.LanguageCode,
                    Title = audioStream.Title ?? string.Empty
                };

                version.Streams.Add(stream);
            }

            // filter to embedded subtitles, but ignore "embedded in video" closed-caption streams
            foreach (PlexStreamResponse subtitleStream in
                     streams.Filter(s => s.StreamType == 3 && s.Index.HasValue && !s.EmbeddedInVideo))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.Subtitle,
                    Index = subtitleStream.Index.Value,
                    Codec = subtitleStream.Codec,
                    Default = subtitleStream.Default,
                    Forced = subtitleStream.Forced,
                    Language = subtitleStream.LanguageCode,
                    Title = subtitleStream.Title
                };

                version.Streams.Add(stream);
            }

            // also include external subtitles
            foreach (PlexStreamResponse subtitleStream in
                     streams.Filter(s => s.StreamType == 3 && !s.Index.HasValue && !string.IsNullOrWhiteSpace(s.Key)))
            {
                var stream = new MediaStream
                {
                    MediaVersionId = version.Id,
                    MediaStreamKind = MediaStreamKind.ExternalSubtitle,

                    // hacky? maybe...
                    FileName = subtitleStream.Key,
                    Index = subtitleStream.Id,

                    Codec = subtitleStream.Codec,
                    Default = subtitleStream.Default,
                    Forced = subtitleStream.Forced,
                    Language = subtitleStream.LanguageCode,

                    Title = subtitleStream.Title
                };

                version.Streams.Add(stream);
            }

            return version;
        });
    }

    private PlexShow ProjectToShow(PlexMetadataResponse response, int mediaSourceId)
    {
        ShowMetadata metadata = ProjectToShowMetadata(response, mediaSourceId);

        var show = new PlexShow
        {
            Key = response.Key,
            Etag = plexEtag.ForShow(response),
            ShowMetadata = new List<ShowMetadata> { metadata },
            TraktListItems = new List<TraktListItem>()
        };

        return show;
    }

    private ShowMetadata ProjectToShowMetadata(PlexMetadataResponse response, int mediaSourceId)
    {
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var metadata = new ShowMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = response.Title,
            SortTitle = SortTitle.GetSortTitle(response.Title),
            Plot = response.Summary,
            Year = response.Year,
            Tagline = response.Tagline,
            ContentRating = response.ContentRating,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            Genres = Optional(response.Genre).Flatten().Map(g => new Genre { Name = g.Tag }).ToList(),
            Tags = new List<Tag>(),
            Studios = new List<Studio>(),
            Actors = Optional(response.Role).Flatten().Map(r => ProjectToModel(r, dateAdded, lastWriteTime))
                .ToList()
        };

        if (response is PlexXmlMetadataResponse xml)
        {
            metadata.Guids = Optional(xml.Guid).Flatten().Map(g => new MetadataGuid { Guid = g.Id }).ToList();
            if (!string.IsNullOrWhiteSpace(xml.PlexGuid))
            {
                Option<string> normalized = NormalizeGuid(xml.PlexGuid);
                foreach (string guid in normalized)
                {
                    if (metadata.Guids.All(g => g.Guid != guid))
                    {
                        metadata.Guids.Add(new MetadataGuid { Guid = guid });
                    }
                }
            }
        }
        else
        {
            metadata.Guids = new List<MetadataGuid>();
        }

        if (!string.IsNullOrWhiteSpace(response.Studio))
        {
            metadata.Studios.Add(new Studio { Name = response.Studio });
        }

        foreach (PlexLabelResponse label in Optional(response.Label).Flatten())
        {
            metadata.Tags.Add(
                new Tag { Name = label.Tag, ExternalCollectionId = label.Id.ToString(CultureInfo.InvariantCulture) });
        }

        if (DateTime.TryParse(response.OriginallyAvailableAt, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            var path = $"plex/{mediaSourceId}{response.Thumb}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        if (!string.IsNullOrWhiteSpace(response.Art))
        {
            var path = $"plex/{mediaSourceId}{response.Art}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        return metadata;
    }

    private PlexSeason ProjectToSeason(PlexXmlMetadataResponse response, int mediaSourceId)
    {
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var metadata = new SeasonMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = response.Title,
            SortTitle = SortTitle.GetSortTitle(response.Title),
            Year = response.Year,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            Tags = new List<Tag>()
        };

        metadata.Guids = Optional(response.Guid).Flatten().Map(g => new MetadataGuid { Guid = g.Id }).ToList();
        if (!string.IsNullOrWhiteSpace(response.PlexGuid))
        {
            Option<string> normalized = NormalizeGuid(response.PlexGuid);
            foreach (string guid in normalized)
            {
                if (metadata.Guids.All(g => g.Guid != guid))
                {
                    metadata.Guids.Add(new MetadataGuid { Guid = guid });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            var path = $"plex/{mediaSourceId}{response.Thumb}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        if (!string.IsNullOrWhiteSpace(response.Art))
        {
            var path = $"plex/{mediaSourceId}{response.Art}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        var season = new PlexSeason
        {
            Key = response.Key,
            Etag = plexEtag.ForSeason(response),
            SeasonNumber = response.Index,
            SeasonMetadata = new List<SeasonMetadata> { metadata },
            TraktListItems = new List<TraktListItem>()
        };

        return season;
    }

    private PlexEpisode ProjectToEpisode(PlexXmlMetadataResponse response, int mediaSourceId)
    {
        PlexMediaResponse<PlexXmlPartResponse> media = response.Media
            .Filter(media => media.Part.Count != 0)
            .MaxBy(media => media.Id);

        PlexXmlPartResponse part = media.Part.Head();
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var version = new MediaVersion
        {
            Name = "Main",
            Duration = TimeSpan.FromMilliseconds(media.Duration),
            Width = media.Width,
            Height = media.Height,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            MediaFiles =
            [
                new PlexMediaFile
                {
                    PlexId = part.Id,
                    Key = part.Key,
                    Path = part.File,
                    PathHash = PathUtils.GetPathHash(part.File)
                }
            ],
            // specifically omit stream details
            Streams = []
        };

        EpisodeMetadata metadata = ProjectToEpisodeMetadata(version, response, mediaSourceId);

        var episode = new PlexEpisode
        {
            Key = response.Key,
            Etag = plexEtag.ForEpisode(response),
            EpisodeMetadata = [metadata],
            MediaVersions = [version],
            TraktListItems = []
        };

        return episode;
    }

    private EpisodeMetadata ProjectToEpisodeMetadata(
        MediaVersion version,
        PlexMetadataResponse response,
        int mediaSourceId)
    {
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var metadata = new EpisodeMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = response.Title,
            SortTitle = SortTitle.GetSortTitle(response.Title),
            EpisodeNumber = response.Index,
            Plot = response.Summary,
            Year = response.Year,
            Tagline = response.Tagline,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            Actors = Optional(response.Role).Flatten().Map(r => ProjectToModel(r, dateAdded, lastWriteTime))
                .ToList(),
            Directors = Optional(response.Director).Flatten().Map(d => new Director { Name = d.Tag }).ToList(),
            Writers = Optional(response.Writer).Flatten().Map(w => new Writer { Name = w.Tag }).ToList(),
            Tags = new List<Tag>(),
            Subtitles = new List<Subtitle>()
        };

        var subtitleStreams = version.Streams
            .Filter(s => s.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
            .ToList();

        metadata.Subtitles.AddRange(subtitleStreams.Map(Subtitle.FromMediaStream));

        if (response is PlexXmlMetadataResponse xml)
        {
            metadata.Guids = Optional(xml.Guid).Flatten().Map(g => new MetadataGuid { Guid = g.Id }).ToList();
            if (!string.IsNullOrWhiteSpace(xml.PlexGuid))
            {
                Option<string> normalized = NormalizeGuid(xml.PlexGuid);
                foreach (string guid in normalized)
                {
                    if (metadata.Guids.All(g => g.Guid != guid))
                    {
                        metadata.Guids.Add(new MetadataGuid { Guid = guid });
                    }
                }
            }
        }
        else
        {
            metadata.Guids = new List<MetadataGuid>();
        }

        if (DateTime.TryParse(response.OriginallyAvailableAt, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            var path = $"plex/{mediaSourceId}{response.Thumb}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.Thumbnail,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        return metadata;
    }

    private static Actor ProjectToModel(PlexRoleResponse role, DateTime dateAdded, DateTime lastWriteTime)
    {
        var actor = new Actor { Name = role.Tag, Role = role.Role };
        if (!string.IsNullOrWhiteSpace(role.Thumb))
        {
            actor.Artwork = new Artwork
            {
                Path = role.Thumb,
                ArtworkKind = ArtworkKind.Thumbnail,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };
        }

        return actor;
    }

    private static MediaChapter ProjectToModel(PlexChapterResponse chapter) =>
        new()
        {
            ChapterId = chapter.Index,
            StartTime = TimeSpan.FromMilliseconds(chapter.StartTimeOffset),
            EndTime = TimeSpan.FromMilliseconds(chapter.EndTimeOffset)
        };

    private PlexOtherVideo ProjectToOtherVideo(PlexMetadataResponse response, int mediaSourceId, PlexLibrary library)
    {
        PlexMediaResponse<PlexPartResponse> media = response.Media
            .Filter(media => media.Part.Count != 0)
            .MaxBy(media => media.Id);

        PlexPartResponse part = media.Part.Head();
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var version = new MediaVersion
        {
            Name = "Main",
            Duration = TimeSpan.FromMilliseconds(media.Duration),
            Width = media.Width,
            Height = media.Height,
            // specifically omit sample aspect ratio
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            MediaFiles =
            [
                new PlexMediaFile
                {
                    PlexId = part.Id,
                    Key = part.Key,
                    Path = part.File,
                    PathHash = PathUtils.GetPathHash(part.File)
                }
            ],
            Streams = []
        };

        OtherVideoMetadata metadata = ProjectToOtherVideoMetadata(version, response, mediaSourceId, library);

        var otherVideo = new PlexOtherVideo
        {
            Etag = plexEtag.ForMovie(response),
            Key = response.Key,
            OtherVideoMetadata = [metadata],
            MediaVersions = [version],
            TraktListItems = []
        };

        return otherVideo;
    }

    private OtherVideoMetadata ProjectToOtherVideoMetadata(
        MediaVersion version,
        PlexMetadataResponse response,
        int mediaSourceId,
        PlexLibrary library)
    {
        DateTime dateAdded = DateTimeOffset.FromUnixTimeSeconds(response.AddedAt).DateTime;
        DateTime lastWriteTime = DateTimeOffset.FromUnixTimeSeconds(response.UpdatedAt).DateTime;

        var metadata = new OtherVideoMetadata
        {
            MetadataKind = MetadataKind.External,
            Title = response.Title,
            SortTitle = SortTitle.GetSortTitle(response.Title),
            Plot = response.Summary,
            Year = response.Year,
            Tagline = response.Tagline,
            ContentRating = response.ContentRating,
            DateAdded = dateAdded,
            DateUpdated = lastWriteTime,
            Genres = Optional(response.Genre).Flatten().Map(g => new Genre { Name = g.Tag }).ToList(),
            Tags = new List<Tag>(),
            Studios = new List<Studio>(),
            Actors = Optional(response.Role).Flatten().Map(r => ProjectToModel(r, dateAdded, lastWriteTime))
                .ToList(),
            Directors = Optional(response.Director).Flatten().Map(d => new Director { Name = d.Tag }).ToList(),
            Writers = Optional(response.Writer).Flatten().Map(w => new Writer { Name = w.Tag }).ToList(),
            Subtitles = new List<Subtitle>()
        };

        var subtitleStreams = version.Streams
            .Filter(s => s.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
            .ToList();

        metadata.Subtitles.AddRange(subtitleStreams.Map(Subtitle.FromMediaStream));

        if (response is PlexXmlMetadataResponse xml)
        {
            metadata.Guids = Optional(xml.Guid).Flatten().Map(g => new MetadataGuid { Guid = g.Id }).ToList();
            if (!string.IsNullOrWhiteSpace(xml.PlexGuid))
            {
                Option<string> normalized = NormalizeGuid(xml.PlexGuid);
                foreach (string guid in normalized)
                {
                    if (metadata.Guids.All(g => g.Guid != guid))
                    {
                        metadata.Guids.Add(new MetadataGuid { Guid = guid });
                    }
                }
            }

            PlexMediaResponse<PlexXmlPartResponse> media = xml.Media
                .Filter(media => media.Part.Count != 0)
                .MaxBy(media => media.Id);

            PlexXmlPartResponse part = media.Part.Head();
            string folder = Path.GetDirectoryName(part.File);

            if (!string.IsNullOrWhiteSpace(folder))
            {
                IEnumerable<string> libraryPaths = library.Paths
                    .HeadOrNone()
                    .Map(p => p.Path)
                    .Map(JsonConvert.DeserializeObject<LibraryPaths>)
                    .Map(lp => lp.Paths)
                    .Flatten();

                // check each library path from plex
                foreach (string libraryPath in libraryPaths)
                {
                    // if the media file belongs to this library path
                    if (folder.StartsWith(libraryPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // try to get a parent directory of the library path
                        string parent = Optional(Directory.GetParent(libraryPath)).Match(
                            di => di.FullName,
                            () => libraryPath);

                        // get all folders between parent and media file
                        string diff = Path.GetRelativePath(parent, folder);

                        // each folder becomes a tag
                        IEnumerable<Tag> tags = diff.Split(Path.DirectorySeparatorChar)
                            .Map(t => new Tag { Name = t });

                        metadata.Tags.AddRange(tags);
                        break;
                    }
                }
            }
        }
        else
        {
            metadata.Guids = new List<MetadataGuid>();
        }

        foreach (PlexLabelResponse label in Optional(response.Label).Flatten())
        {
            metadata.Tags.Add(
                new Tag { Name = label.Tag, ExternalCollectionId = label.Id.ToString(CultureInfo.InvariantCulture) });
        }

        if (!string.IsNullOrWhiteSpace(response.Studio))
        {
            metadata.Studios.Add(new Studio { Name = response.Studio });
        }

        if (DateTime.TryParse(response.OriginallyAvailableAt, out DateTime releaseDate))
        {
            metadata.ReleaseDate = releaseDate;
        }

        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            var path = $"plex/{mediaSourceId}{response.Thumb}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.Poster,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        if (!string.IsNullOrWhiteSpace(response.Art))
        {
            var path = $"plex/{mediaSourceId}{response.Art}";
            var artwork = new Artwork
            {
                ArtworkKind = ArtworkKind.FanArt,
                Path = path,
                DateAdded = dateAdded,
                DateUpdated = lastWriteTime
            };

            metadata.Artwork ??= new List<Artwork>();
            metadata.Artwork.Add(artwork);
        }

        return metadata;
    }

    private Option<string> NormalizeGuid(string guid)
    {
        if (guid.StartsWith("plex://show", StringComparison.OrdinalIgnoreCase) ||
            guid.StartsWith("plex://season", StringComparison.OrdinalIgnoreCase) ||
            guid.StartsWith("plex://episode", StringComparison.OrdinalIgnoreCase) ||
            guid.StartsWith("plex://movie", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }

        if (guid.StartsWith("com.plexapp.agents.imdb", StringComparison.OrdinalIgnoreCase))
        {
            string strip1 = guid.Replace("com.plexapp.agents.imdb://", string.Empty);
            string strip2 = strip1.Split("?").Head();
            return $"imdb://{strip2}";
        }

        if (guid.StartsWith("com.plexapp.agents.thetvdb", StringComparison.OrdinalIgnoreCase))
        {
            string strip1 = guid.Replace("com.plexapp.agents.thetvdb://", string.Empty);
            string strip2 = strip1.Split("?").Head();
            return $"tvdb://{strip2}";
        }

        if (guid.StartsWith("com.plexapp.agents.themoviedb", StringComparison.OrdinalIgnoreCase))
        {
            string strip1 = guid.Replace("com.plexapp.agents.themoviedb://", string.Empty);
            string strip2 = strip1.Split("?").Head();
            return $"tmdb://{strip2}";
        }

        if (guid.StartsWith("local://", StringComparison.OrdinalIgnoreCase) ||
            guid.StartsWith("com.plexapp.agents.none://", StringComparison.OrdinalIgnoreCase))
        {
            // _logger.LogDebug("Ignoring local Plex guid: {Guid}", guid);
        }
        else
        {
            logger.LogWarning("Unsupported guid format from Plex; ignoring: {Guid}", guid);
        }

        return None;
    }

    private sealed class LibraryPaths
    {
        [JsonProperty("paths")]
        public List<string> Paths { get; set; } = [];
    }
}
