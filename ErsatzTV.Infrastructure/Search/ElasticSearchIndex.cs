using System.Globalization;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Search;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.Infrastructure.Search.Models;
using Microsoft.Extensions.Logging;
using ExistsResponse = Elastic.Clients.Elasticsearch.IndexManagement.ExistsResponse;
using MediaStream = ErsatzTV.Core.Domain.MediaStream;
using Query = Lucene.Net.Search.Query;
using ES = Elastic.Clients.Elasticsearch;

namespace ErsatzTV.Infrastructure.Search;

public class ElasticSearchIndex : ISearchIndex
{
    private readonly List<CultureInfo> _cultureInfos;
    private readonly ILogger<ElasticSearchIndex> _logger;

    private readonly SearchQueryParser _searchQueryParser;
    private ES.ElasticsearchClient _client;

    public ElasticSearchIndex(SearchQueryParser searchQueryParser, ILogger<ElasticSearchIndex> logger)
    {
        _searchQueryParser = searchQueryParser;
        _logger = logger;
        _cultureInfos = CultureInfo.GetCultures(CultureTypes.NeutralCultures).ToList();
    }

    public static Uri Uri { get; set; }
    public static string IndexName { get; set; }

    public void Dispose() =>
        // do nothing
        GC.SuppressFinalize(this);

    public async Task<bool> IndexExists()
    {
        _client ??= CreateClient();
        ExistsResponse exists = await _client.Indices.ExistsAsync(IndexName);
        return exists.IsValidResponse;
    }

    public int Version => 49;

    public async Task<bool> Initialize(
        ILocalFileSystem localFileSystem,
        IConfigElementRepository configElementRepository,
        CancellationToken cancellationToken)
    {
        _client ??= CreateClient();

        ExistsResponse exists = await _client.Indices.ExistsAsync(IndexName, cancellationToken);
        if (!exists.IsValidResponse)
        {
            CreateIndexResponse createResponse = await CreateIndex();
            return createResponse.IsValidResponse;
        }

        return true;
    }

    public async Task<Unit> Rebuild(
        ISearchRepository searchRepository,
        IFallbackMetadataProvider fallbackMetadataProvider,
        ILanguageCodeService languageCodeService,
        CancellationToken cancellationToken)
    {
        DeleteIndexResponse deleteResponse = await _client.Indices.DeleteAsync(IndexName, cancellationToken);
        if (!deleteResponse.IsValidResponse)
        {
            return Unit.Default;
        }

        CreateIndexResponse createResponse = await CreateIndex();
        if (!createResponse.IsValidResponse)
        {
            return Unit.Default;
        }

        await foreach (MediaItem mediaItem in searchRepository.GetAllMediaItems(cancellationToken))
        {
            await RebuildItem(searchRepository, fallbackMetadataProvider, languageCodeService, mediaItem);
        }

        return Unit.Default;
    }

    public async Task<Unit> RebuildItems(
        ISearchRepository searchRepository,
        IFallbackMetadataProvider fallbackMetadataProvider,
        ILanguageCodeService languageCodeService,
        IEnumerable<int> itemIds,
        CancellationToken cancellationToken)
    {
        foreach (int id in itemIds)
        {
            foreach (MediaItem mediaItem in await searchRepository.GetItemToIndex(id, cancellationToken))
            {
                await RebuildItem(searchRepository, fallbackMetadataProvider, languageCodeService, mediaItem);
            }
        }

        return Unit.Default;
    }

    public async Task<Unit> UpdateItems(
        ISearchRepository searchRepository,
        IFallbackMetadataProvider fallbackMetadataProvider,
        ILanguageCodeService languageCodeService,
        List<MediaItem> items)
    {
        foreach (MediaItem item in items)
        {
            switch (item)
            {
                case Movie movie:
                    await UpdateMovie(languageCodeService, movie);
                    break;
                case Show show:
                    await UpdateShow(searchRepository, languageCodeService, show);
                    break;
                case Season season:
                    await UpdateSeason(searchRepository, languageCodeService, season);
                    break;
                case Artist artist:
                    await UpdateArtist(searchRepository, languageCodeService, artist);
                    break;
                case MusicVideo musicVideo:
                    await UpdateMusicVideo(languageCodeService, musicVideo);
                    break;
                case Episode episode:
                    await UpdateEpisode(languageCodeService, fallbackMetadataProvider, episode);
                    break;
                case OtherVideo otherVideo:
                    await UpdateOtherVideo(languageCodeService, otherVideo);
                    break;
                case Song song:
                    await UpdateSong(languageCodeService, song);
                    break;
                case Image image:
                    await UpdateImage(languageCodeService, image);
                    break;
            }
        }

        return Unit.Default;
    }

    public async Task<bool> RemoveItems(IEnumerable<int> ids)
    {
        var deleteBulkRequest = new ES.BulkRequest { Operations = [] };
        foreach (int id in ids)
        {
            var deleteOperation = new BulkDeleteOperation<ElasticSearchItem>(new ES.Id(id)) { Index = IndexName };
            deleteBulkRequest.Operations.Add(deleteOperation);
        }

        ES.BulkResponse deleteResponse = await _client.BulkAsync(deleteBulkRequest).ConfigureAwait(false);
        return deleteResponse.IsValidResponse;
    }

    public Task<SearchResult> Search(
        string query,
        string smartCollectionName,
        int skip,
        int limit,
        CancellationToken cancellationToken) => Search(
        query,
        smartCollectionName,
        skip,
        limit,
        [LuceneSearchIndex.TitleField],
        cancellationToken);

    public async Task<SearchResult> Search(
        string query,
        string smartCollectionName,
        int skip,
        int limit,
        List<string> defaultFields,
        CancellationToken cancellationToken)
    {
        var items = new List<MinimalElasticSearchItem>();
        var totalCount = 0;

        Query parsedQuery = await _searchQueryParser.ParseQuery(
            query,
            smartCollectionName,
            defaultFields,
            cancellationToken);

        ES.SearchResponse<MinimalElasticSearchItem> response = await _client.SearchAsync<MinimalElasticSearchItem>(
            s => s.Indices(IndexName)
                .Sort(ss => ss.Field(f => f.SortTitle, fs => fs.Order(ES.SortOrder.Asc)))
                .From(skip)
                .Size(limit)
                .QueryLuceneSyntax(parsedQuery.ToString()),
            cancellationToken);

        if (response.IsValidResponse)
        {
            items.AddRange(response.Documents);
            totalCount = (int)response.Total;
        }

        var searchResult = new SearchResult(items.Map(i => new SearchItem(i.Type, i.Id)).ToList(), totalCount);

        if (limit is > 0 and < 10_000)
        {
            searchResult.PageMap = await GetSearchPageMap(query, limit);
        }

        return searchResult;
    }

    public void Commit()
    {
        // do nothing
    }

    private static ES.ElasticsearchClient CreateClient()
    {
        ES.ElasticsearchClientSettings settings = new ES.ElasticsearchClientSettings(Uri).DefaultIndex(IndexName);
        return new ES.ElasticsearchClient(settings);
    }

    private async Task<CreateIndexResponse> CreateIndex() =>
        await _client.Indices.CreateAsync<ElasticSearchItem>(
            IndexName,
            i => i.Mappings(m => m.Properties(p => p
                .Keyword(t => t.Type, t => t.Store())
                .Text(t => t.Title, t => t.Store(false))
                .Keyword(t => t.SortTitle, t => t.Store(false))
                .Text(t => t.LibraryName, t => t.Store(false))
                .Keyword(t => t.LibraryId, t => t.Store(false))
                .Keyword(t => t.TitleAndYear, t => t.Store(false))
                .Text(t => t.TitleAndYearSearch, t => t.Store(false))
                .Keyword(t => t.JumpLetter, t => t.Store())
                .Keyword(t => t.State, t => t.Store(false))
                .Text(t => t.MetadataKind, t => t.Store(false))
                .Text(t => t.Language, t => t.Store(false))
                .Text(t => t.LanguageTag, t => t.Store(false))
                .Text(t => t.SubLanguage, t => t.Store(false))
                .Text(t => t.SubLanguageTag, t => t.Store(false))
                .IntegerNumber(t => t.Height, t => t.Store(false))
                .IntegerNumber(t => t.Width, t => t.Store(false))
                .Keyword(t => t.VideoCodec, t => t.Store(false))
                .IntegerNumber(t => t.VideoBitDepth, t => t.Store(false))
                .Keyword(t => t.VideoDynamicRange, t => t.Store(false))
                .Keyword(t => t.ContentRating, t => t.Store(false))
                .Keyword(t => t.ReleaseDate, t => t.Store(false))
                .Keyword(t => t.AddedDate, t => t.Store(false))
                .Text(t => t.Plot, t => t.Store(false))
                .Text(t => t.Genre, t => t.Store(false))
                .Text(t => t.Tag, t => t.Store(false))
                .Keyword(t => t.TagFull, t => t.Store(false))
                .Text(t => t.Studio, t => t.Store(false))
                .Text(t => t.Network, t => t.Store(false))
                .Text(t => t.Actor, t => t.Store(false))
                .Text(t => t.Director, t => t.Store(false))
                .Text(t => t.Writer, t => t.Store(false))
                .Keyword(t => t.TraktList, t => t.Store(false))
                .IntegerNumber(t => t.SeasonNumber, t => t.Store(false))
                .Text(t => t.ShowTitle, t => t.Store(false))
                .Text(t => t.ShowGenre, t => t.Store(false))
                .Text(t => t.ShowTag, t => t.Store(false))
                .Text(t => t.ShowStudio, t => t.Store(false))
                .Text(t => t.ShowNetwork, t => t.Store(false))
                .Keyword(t => t.ShowContentRating, t => t.Store(false))
                .Text(t => t.Style, t => t.Store(false))
                .Text(t => t.Mood, t => t.Store(false))
                .Text(t => t.Album, t => t.Store(false))
                .Text(t => t.Artist, t => t.Store(false))
                .IntegerNumber(t => t.EpisodeNumber, t => t.Store(false))
                .Text(t => t.AlbumArtist, t => t.Store(false))
            )));

    private async Task RebuildItem(
        ISearchRepository searchRepository,
        IFallbackMetadataProvider fallbackMetadataProvider,
        ILanguageCodeService languageCodeService,
        MediaItem mediaItem)
    {
        switch (mediaItem)
        {
            case Movie movie:
                await UpdateMovie(languageCodeService, movie);
                break;
            case Show show:
                await UpdateShow(searchRepository, languageCodeService, show);
                break;
            case Season season:
                await UpdateSeason(searchRepository, languageCodeService, season);
                break;
            case Artist artist:
                await UpdateArtist(searchRepository, languageCodeService, artist);
                break;
            case MusicVideo musicVideo:
                await UpdateMusicVideo(languageCodeService, musicVideo);
                break;
            case Episode episode:
                await UpdateEpisode(languageCodeService, fallbackMetadataProvider, episode);
                break;
            case OtherVideo otherVideo:
                await UpdateOtherVideo(languageCodeService, otherVideo);
                break;
            case Song song:
                await UpdateSong(languageCodeService, song);
                break;
        }
    }

    private async Task UpdateMovie(ILanguageCodeService languageCodeService, Movie movie)
    {
        foreach (MovieMetadata metadata in movie.MovieMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = movie.Id,
                    Type = LuceneSearchIndex.MovieType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = movie.LibraryPath.Library.Name,
                    LibraryId = movie.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = movie.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, movie.MediaVersions),
                    LanguageTag = GetLanguageTags(movie.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, movie.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(movie.MediaVersions),
                    ContentRating = GetContentRatings(metadata.ContentRating),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Plot = metadata.Plot ?? string.Empty,
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Where(Tag.IsSearchTag).Map(t => t.Name)
                        .ToList(),
                    TagFull = metadata.Tags.Where(Tag.IsSearchTag).Map(t => t.Name)
                        .ToList(),
                    Country = metadata.Tags.Where(t => t.ExternalTypeId == Tag.NfoCountryTypeId).Map(t => t.Name)
                        .ToList(),
                    Studio = metadata.Studios.Map(s => s.Name).ToList(),
                    Actor = metadata.Actors.Map(a => a.Name).ToList(),
                    Director = metadata.Directors.Map(d => d.Name).ToList(),
                    Writer = metadata.Writers.Map(w => w.Name).ToList(),
                    TraktList = movie.TraktListItems
                        .Map(t => t.TraktList.TraktId.ToString(CultureInfo.InvariantCulture)).ToList()
                };

                AddStatistics(doc, movie.MediaVersions);
                AddCollections(doc, movie.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Movie = null;
                _logger.LogWarning(ex, "Error indexing movie with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateShow(ISearchRepository searchRepository, ILanguageCodeService languageCodeService, Show show)
    {
        foreach (ShowMetadata metadata in show.ShowMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = show.Id,
                    Type = LuceneSearchIndex.ShowType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = show.LibraryPath.Library.Name,
                    LibraryId = show.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = show.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, await searchRepository.GetLanguagesForShow(show)),
                    LanguageTag = await searchRepository.GetLanguagesForShow(show),
                    SubLanguage = GetLanguages(
                        languageCodeService,
                        await searchRepository.GetSubLanguagesForShow(show)),
                    SubLanguageTag = await searchRepository.GetSubLanguagesForShow(show),
                    ContentRating = GetContentRatings(metadata.ContentRating),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Plot = metadata.Plot ?? string.Empty,
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Where(Tag.IsSearchTag).Map(t => t.Name)
                        .ToList(),
                    TagFull = metadata.Tags.Where(Tag.IsSearchTag).Map(t => t.Name)
                        .ToList(),
                    Studio = metadata.Studios.Map(s => s.Name).ToList(),
                    Network = metadata.Tags.Where(t => t.ExternalTypeId == Tag.PlexNetworkTypeId).Map(t => t.Name)
                        .ToList(),
                    Actor = metadata.Actors.Map(a => a.Name).ToList(),
                    TraktList = show.TraktListItems.Map(t => t.TraktList.TraktId.ToString(CultureInfo.InvariantCulture))
                        .ToList()
                };

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Show = null;
                _logger.LogWarning(ex, "Error indexing show with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateSeason(
        ISearchRepository searchRepository,
        ILanguageCodeService languageCodeService,
        Season season)
    {
        foreach (SeasonMetadata metadata in season.SeasonMetadata.HeadOrNone())
        foreach (ShowMetadata showMetadata in season.Show.ShowMetadata.HeadOrNone())
        {
            try
            {
                var seasonTitle = $"{showMetadata.Title} - S{season.SeasonNumber}";
                string sortTitle = $"{showMetadata.SortTitle}_{season.SeasonNumber:0000}"
                    .ToLowerInvariant();
                string titleAndYear = $"{showMetadata.Title}_{showMetadata.Year}_{season.SeasonNumber}"
                    .ToLowerInvariant();

                var doc = new ElasticSearchItem
                {
                    Id = season.Id,
                    Type = LuceneSearchIndex.SeasonType,
                    Title = seasonTitle,
                    SortTitle = sortTitle,
                    LibraryName = season.LibraryPath.Library.Name,
                    LibraryId = season.LibraryPath.Library.Id,
                    TitleAndYear = titleAndYear,
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(showMetadata),
                    State = season.State.ToString(),
                    SeasonNumber = season.SeasonNumber,
                    ShowTitle = showMetadata.Title,
                    ShowGenre = showMetadata.Genres.Map(g => g.Name).ToList(),
                    ShowTag = showMetadata.Tags.Map(t => t.Name).ToList(),
                    ShowStudio = showMetadata.Studios.Map(s => s.Name).ToList(),
                    ShowContentRating = GetContentRatings(showMetadata.ContentRating),
                    Language = GetLanguages(
                        languageCodeService,
                        await searchRepository.GetLanguagesForSeason(season)),
                    LanguageTag = await searchRepository.GetLanguagesForSeason(season),
                    SubLanguage = GetLanguages(
                        languageCodeService,
                        await searchRepository.GetSubLanguagesForSeason(season)),
                    SubLanguageTag = await searchRepository.GetSubLanguagesForSeason(season),
                    ContentRating = GetContentRatings(showMetadata.ContentRating),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    TraktList = season.TraktListItems
                        .Map(t => t.TraktList.TraktId.ToString(CultureInfo.InvariantCulture)).ToList(),
                    Tag = metadata.Tags.Map(a => a.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList()
                };

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Season = null;
                _logger.LogWarning(ex, "Error indexing season with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateArtist(
        ISearchRepository searchRepository,
        ILanguageCodeService languageCodeService,
        Artist artist)
    {
        foreach (ArtistMetadata metadata in artist.ArtistMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = artist.Id,
                    Type = LuceneSearchIndex.ArtistType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = artist.LibraryPath.Library.Name,
                    LibraryId = artist.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = artist.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(
                        languageCodeService,
                        await searchRepository.GetLanguagesForArtist(artist)),
                    LanguageTag = await searchRepository.GetLanguagesForArtist(artist),
                    SubLanguage = GetLanguages(
                        languageCodeService,
                        await searchRepository.GetSubLanguagesForArtist(artist)),
                    SubLanguageTag = await searchRepository.GetSubLanguagesForArtist(artist),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Style = metadata.Styles.Map(t => t.Name).ToList(),
                    Mood = metadata.Moods.Map(s => s.Name).ToList()
                };

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Artist = null;
                _logger.LogWarning(ex, "Error indexing artist with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateMusicVideo(ILanguageCodeService languageCodeService, MusicVideo musicVideo)
    {
        foreach (MusicVideoMetadata metadata in musicVideo.MusicVideoMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = musicVideo.Id,
                    Type = LuceneSearchIndex.MusicVideoType,
                    Title = metadata.Title,
                    SortTitle = (metadata.SortTitle ?? string.Empty).ToLowerInvariant(),
                    LibraryName = musicVideo.LibraryPath.Library.Name,
                    LibraryId = musicVideo.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = musicVideo.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, musicVideo.MediaVersions),
                    LanguageTag = GetLanguageTags(musicVideo.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, musicVideo.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(musicVideo.MediaVersions),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Album = metadata.Album ?? string.Empty,
                    Plot = metadata.Plot ?? string.Empty,
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Map(t => t.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList(),
                    Studio = metadata.Studios.Map(s => s.Name).ToList()
                };

                var artists = new System.Collections.Generic.HashSet<string>();

                if (musicVideo.Artist != null)
                {
                    foreach (ArtistMetadata artistMetadata in musicVideo.Artist.ArtistMetadata)
                    {
                        artists.Add(artistMetadata.Title);
                    }
                }

                foreach (MusicVideoArtist artist in metadata.Artists)
                {
                    artists.Add(artist.Name);
                }

                doc.Artist = artists.ToList();

                AddStatistics(doc, musicVideo.MediaVersions);
                AddCollections(doc, musicVideo.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.MusicVideo = null;
                _logger.LogWarning(ex, "Error indexing music video with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateEpisode(
        ILanguageCodeService languageCodeService,
        IFallbackMetadataProvider fallbackMetadataProvider,
        Episode episode)
    {
        // try to load metadata here, since episodes without metadata won't index
        if (episode.EpisodeMetadata.Count == 0)
        {
            episode.EpisodeMetadata ??= new List<EpisodeMetadata>();
            episode.EpisodeMetadata = fallbackMetadataProvider.GetFallbackMetadata(episode);
            foreach (EpisodeMetadata metadata in episode.EpisodeMetadata)
            {
                metadata.Episode = episode;
            }
        }

        foreach (EpisodeMetadata metadata in episode.EpisodeMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = episode.Id,
                    Type = LuceneSearchIndex.EpisodeType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = episode.LibraryPath.Library.Name,
                    LibraryId = episode.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = episode.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    SeasonNumber = episode.Season?.SeasonNumber ?? 0,
                    EpisodeNumber = metadata.EpisodeNumber,
                    Language = GetLanguages(languageCodeService, episode.MediaVersions),
                    LanguageTag = GetLanguageTags(episode.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, episode.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(episode.MediaVersions),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Plot = metadata.Plot ?? string.Empty,
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Map(t => t.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList(),
                    Studio = metadata.Studios.Map(s => s.Name).ToList(),
                    Actor = metadata.Actors.Map(a => a.Name).ToList(),
                    Director = metadata.Directors.Map(d => d.Name).ToList(),
                    Writer = metadata.Writers.Map(w => w.Name).ToList(),
                    TraktList = episode.TraktListItems
                        .Map(t => t.TraktList.TraktId.ToString(CultureInfo.InvariantCulture)).ToList()
                };

                // add some show fields to help filter episodes within a particular show
                foreach (ShowMetadata showMetadata in Optional(episode.Season?.Show?.ShowMetadata).Flatten())
                {
                    doc.ShowTitle = showMetadata.Title;
                    doc.ShowGenre = showMetadata.Genres.Map(g => g.Name).ToList();
                    doc.ShowTag = showMetadata.Tags.Where(Tag.IsSearchTag)
                        .Map(t => t.Name).ToList();
                    doc.ShowStudio = showMetadata.Studios.Map(s => s.Name).ToList();
                    doc.ShowNetwork = showMetadata.Tags.Where(t => t.ExternalTypeId == Tag.PlexNetworkTypeId)
                        .Map(t => t.Name).ToList();
                    doc.ShowContentRating = GetContentRatings(showMetadata.ContentRating);
                }

                AddStatistics(doc, episode.MediaVersions);
                AddCollections(doc, episode.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Episode = null;
                _logger.LogWarning(ex, "Error indexing episode with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateOtherVideo(ILanguageCodeService languageCodeService, OtherVideo otherVideo)
    {
        foreach (OtherVideoMetadata metadata in otherVideo.OtherVideoMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = otherVideo.Id,
                    Type = LuceneSearchIndex.OtherVideoType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = otherVideo.LibraryPath.Library.Name,
                    LibraryId = otherVideo.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = otherVideo.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, otherVideo.MediaVersions),
                    LanguageTag = GetLanguageTags(otherVideo.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, otherVideo.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(otherVideo.MediaVersions),
                    ContentRating = GetContentRatings(metadata.ContentRating),
                    ReleaseDate = GetReleaseDate(metadata.ReleaseDate),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Plot = metadata.Plot ?? string.Empty,
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Map(t => t.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList(),
                    Studio = metadata.Studios.Map(s => s.Name).ToList(),
                    Actor = metadata.Actors.Map(a => a.Name).ToList(),
                    Director = metadata.Directors.Map(d => d.Name).ToList(),
                    Writer = metadata.Writers.Map(w => w.Name).ToList()
                };

                AddStatistics(doc, otherVideo.MediaVersions);
                AddCollections(doc, otherVideo.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.OtherVideo = null;
                _logger.LogWarning(ex, "Error indexing other video with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateSong(ILanguageCodeService languageCodeService, Song song)
    {
        foreach (SongMetadata metadata in song.SongMetadata.HeadOrNone())
        {
            try
            {
                metadata.AlbumArtists ??= [];
                metadata.Artists ??= [];

                var doc = new ElasticSearchItem
                {
                    Id = song.Id,
                    Type = LuceneSearchIndex.SongType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = song.LibraryPath.Library.Name,
                    LibraryId = song.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = song.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, song.MediaVersions),
                    LanguageTag = GetLanguageTags(song.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, song.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(song.MediaVersions),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Album = metadata.Album ?? string.Empty,
                    Artist = metadata.Artists.ToList(),
                    AlbumArtist = metadata.AlbumArtists.ToList(),
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Map(t => t.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList()
                };

                AddStatistics(doc, song.MediaVersions);
                AddCollections(doc, song.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Song = null;
                _logger.LogWarning(ex, "Error indexing song with metadata {@Metadata}", metadata);
            }
        }
    }

    private async Task UpdateImage(ILanguageCodeService languageCodeService, Image image)
    {
        foreach (ImageMetadata metadata in image.ImageMetadata.HeadOrNone())
        {
            try
            {
                var doc = new ElasticSearchItem
                {
                    Id = image.Id,
                    Type = LuceneSearchIndex.ImageType,
                    Title = metadata.Title,
                    SortTitle = metadata.SortTitle.ToLowerInvariant(),
                    LibraryName = image.LibraryPath.Library.Name,
                    LibraryId = image.LibraryPath.Library.Id,
                    TitleAndYear = LuceneSearchIndex.GetTitleAndYear(metadata),
                    TitleAndYearSearch = LuceneSearchIndex.GetTitleAndYearSearch(metadata),
                    JumpLetter = LuceneSearchIndex.GetJumpLetter(metadata),
                    State = image.State.ToString(),
                    MetadataKind = metadata.MetadataKind.ToString(),
                    Language = GetLanguages(languageCodeService, image.MediaVersions),
                    LanguageTag = GetLanguageTags(image.MediaVersions),
                    SubLanguage = GetSubLanguages(languageCodeService, image.MediaVersions),
                    SubLanguageTag = GetSubLanguageTags(image.MediaVersions),
                    AddedDate = GetAddedDate(metadata.DateAdded),
                    Genre = metadata.Genres.Map(g => g.Name).ToList(),
                    Tag = metadata.Tags.Map(t => t.Name).ToList(),
                    TagFull = metadata.Tags.Map(t => t.Name).ToList()
                };

                IEnumerable<int> libraryFolderIds = image.MediaVersions
                    .SelectMany(mv => mv.MediaFiles)
                    .SelectMany(mf => Optional(mf.LibraryFolderId));

                foreach (int libraryFolderId in libraryFolderIds)
                {
                    doc.LibraryFolderId = libraryFolderId;
                }

                AddStatistics(doc, image.MediaVersions);
                AddCollections(doc, image.Collections);

                foreach ((string key, List<string> value) in GetMetadataGuids(metadata))
                {
                    doc.AdditionalProperties.Add(key, value);
                }

                await _client.IndexAsync(doc, IndexName, ES.Id.From(doc));
            }
            catch (Exception ex)
            {
                metadata.Image = null;
                _logger.LogWarning(ex, "Error indexing image with metadata {@Metadata}", metadata);
            }
        }
    }

    private static string GetReleaseDate(DateTime? metadataReleaseDate) =>
        metadataReleaseDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string GetAddedDate(DateTime metadataAddedDate) =>
        metadataAddedDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static List<string> GetContentRatings(string metadataContentRating)
    {
        var contentRatings = new List<string>();

        if (!string.IsNullOrWhiteSpace(metadataContentRating))
        {
            foreach (string contentRating in metadataContentRating.Split("/")
                         .Map(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                contentRatings.Add(contentRating);
            }
        }

        return contentRatings;
    }

    private List<string> GetLanguages(ILanguageCodeService languageCodeService, IEnumerable<MediaVersion> mediaVersions)
    {
        var result = new List<string>();

        foreach (MediaVersion version in mediaVersions.HeadOrNone())
        {
            var mediaCodes = version.Streams
                .Filter(ms => ms.MediaStreamKind == MediaStreamKind.Audio)
                .Map(ms => ms.Language)
                .Distinct()
                .ToList();

            result.AddRange(GetLanguages(languageCodeService, mediaCodes));
        }

        return result;
    }

    private List<string> GetSubLanguages(
        ILanguageCodeService languageCodeService,
        IEnumerable<MediaVersion> mediaVersions)
    {
        var result = new List<string>();

        foreach (MediaVersion version in mediaVersions.HeadOrNone())
        {
            var mediaCodes = version.Streams
                .Filter(ms => ms.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
                .Map(ms => ms.Language)
                .Distinct()
                .ToList();

            result.AddRange(GetLanguages(languageCodeService, mediaCodes));
        }

        return result;
    }

    private List<string> GetLanguages(ILanguageCodeService languageCodeService, List<string> mediaCodes)
    {
        var englishNames = new System.Collections.Generic.HashSet<string>();
        foreach (string code in languageCodeService.GetAllLanguageCodes(mediaCodes))
        {
            Option<CultureInfo> maybeCultureInfo = _cultureInfos.Find(ci => string.Equals(
                ci.ThreeLetterISOLanguageName,
                code,
                StringComparison.OrdinalIgnoreCase));
            foreach (CultureInfo cultureInfo in maybeCultureInfo)
            {
                englishNames.Add(cultureInfo.EnglishName);
            }
        }

        return englishNames.ToList();
    }

    private static List<string> GetLanguageTags(IEnumerable<MediaVersion> mediaVersions) =>
        mediaVersions
            .Map(mv => mv.Streams.Filter(ms => ms.MediaStreamKind == MediaStreamKind.Audio).Map(ms => ms.Language))
            .Flatten()
            .Filter(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

    private static List<string> GetSubLanguageTags(IEnumerable<MediaVersion> mediaVersions) =>
        mediaVersions
            .Map(mv => mv.Streams
                .Filter(ms => ms.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
                .Map(ms => ms.Language))
            .Flatten()
            .Filter(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

    private static void AddStatistics(ElasticSearchItem doc, IEnumerable<MediaVersion> mediaVersions)
    {
        foreach (MediaVersion version in mediaVersions.HeadOrNone())
        {
            doc.Chapters = (version.Chapters ?? []).Count;
            doc.Minutes = (int)Math.Ceiling(version.Duration.TotalMinutes);
            doc.Seconds = (int)Math.Ceiling(version.Duration.TotalSeconds);

            foreach (MediaStream videoStream in version.Streams
                         .Filter(s => s.MediaStreamKind == MediaStreamKind.Video)
                         .HeadOrNone())
            {
                doc.Height = version.Height;
                doc.Width = version.Width;

                if (!string.IsNullOrWhiteSpace(videoStream.Codec))
                {
                    doc.VideoCodec = videoStream.Codec;
                }

                Option<IPixelFormat> maybePixelFormat =
                    AvailablePixelFormats.ForPixelFormat(videoStream.PixelFormat, null);
                foreach (IPixelFormat pixelFormat in maybePixelFormat)
                {
                    doc.VideoBitDepth = pixelFormat.BitDepth;
                }

                if (maybePixelFormat.IsNone && videoStream.BitsPerRawSample > 0)
                {
                    doc.VideoBitDepth = videoStream.BitsPerRawSample;
                }

                var colorParams = new ColorParams(
                    videoStream.ColorRange,
                    videoStream.ColorSpace,
                    videoStream.ColorTransfer,
                    videoStream.ColorPrimaries);

                doc.VideoDynamicRange = colorParams.IsHdr ? "hdr" : "sdr";
            }
        }
    }

    private static void AddCollections(ElasticSearchItem doc, IEnumerable<Collection> collections)
    {
        doc.Collection ??= [];

        foreach (Collection collection in collections)
        {
            doc.Collection.Add(collection.Name);
        }
    }

    private static Dictionary<string, List<string>> GetMetadataGuids(Core.Domain.Metadata metadata)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (MetadataGuid guid in metadata.Guids)
        {
            string[] split = (guid.Guid ?? string.Empty).Split("://");
            if (split.Length == 2 && !string.IsNullOrWhiteSpace(split[1]))
            {
                string key = split[0];
                string v2 = split[1].ToLowerInvariant();
                result.TryAdd(key, new List<string>());
                result[key].Add(v2);
            }
        }

        return result;
    }

    private async Task<SearchPageMap> GetSearchPageMap(string query, int limit)
    {
        ES.SearchResponse<MinimalElasticSearchItem> response = await _client.SearchAsync<MinimalElasticSearchItem>(s =>
            s
                .Indices(IndexName)
                .Size(0)
                .Sort(ss => ss.Field(f => f.SortTitle, fs => fs.Order(ES.SortOrder.Asc)))
                .Aggregations(a => a.Add("count", agg => agg.Terms(v => v.Field(i => i.JumpLetter).Size(30))))
                .QueryLuceneSyntax(query));

        if (!response.IsValidResponse)
        {
            return null;
        }

        var letters = new List<char>
        {
            '#', 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            'u', 'v', 'w', 'x', 'y', 'z'
        };
        var map = letters.ToDictionary(letter => letter, _ => 0);

        if (response.Aggregations?.Values.Head() is StringTermsAggregate aggregate)
        {
            // start on page 1
            int total = limit;

            foreach (char letter in letters)
            {
                map[letter] = total / limit;

                Option<StringTermsBucket> maybeBucket = aggregate.Buckets.Find(b => b.Key == letter.ToString());
                total += maybeBucket.Sum(bucket => (int)bucket.DocCount);
            }
        }

        return new SearchPageMap(map);
    }
}
