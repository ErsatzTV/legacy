using ErsatzTV.Infrastructure.Jellyfin.Models;
using Refit;

namespace ErsatzTV.Infrastructure.Jellyfin;

// collapseBoxSetItems=false: Jellyfin 12 collapses collection members into their BoxSet
// for queries without a user, regardless of server grouping settings
[Headers("Accept: application/json")]
public interface IJellyfinApi
{
    [Get("/System/Info")]
    Task<JellyfinSystemInformationResponse> GetSystemInformation(
        [Header("Authorization")]
        string authorizationHeader,
        CancellationToken cancellationToken);

    [Get("/Users")]
    Task<List<JellyfinUserResponse>> GetUsers(
        [Header("Authorization")]
        string authorizationHeader);

    [Get("/Library/VirtualFolders")]
    Task<List<JellyfinLibraryResponse>> GetLibraries(
        [Header("Authorization")]
        string authorizationHeader);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetMovieLibraryItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields =
            "Path,Genres,Tags,DateCreated,Etag,Overview,Taglines,Studios,People,OfficialRating,ProviderIds,Chapters",
        [Query]
        string includeItemTypes = "Movie",
        [Query]
        bool recursive = true,
        [Query]
        string filters = "IsNotFolder",
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetShowLibraryItemsWithoutPeople(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields =
            "Path,Genres,Tags,DateCreated,Etag,Overview,Taglines,Studios,OfficialRating,ProviderIds",
        [Query]
        string includeItemTypes = "Series",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0,
        [Query]
        string ids = null);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetShowLibraryItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields =
            "Path,Genres,Tags,DateCreated,Etag,Overview,Taglines,Studios,People,OfficialRating,ProviderIds",
        [Query]
        string includeItemTypes = "Series",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0,
        [Query]
        string ids = null);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetSeasonLibraryItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields = "Path,DateCreated,Etag,Taglines,ProviderIds",
        [Query]
        string includeItemTypes = "Season",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetEpisodeLibraryItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields = "Path,Genres,Tags,DateCreated,Etag,Overview,ProviderIds,People,Chapters",
        [Query]
        string includeItemTypes = "Episode",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0,
        [Query]
        string ids = null);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetEpisodeLibraryItemsWithoutPeople(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields = "Path,Genres,Tags,DateCreated,Etag,Overview,ProviderIds,Chapters",
        [Query]
        string includeItemTypes = "Episode",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetCollectionLibraryItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields = "Etag",
        [Query]
        string includeItemTypes = "BoxSet",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0);

    [Get("/Items?sortOrder=Ascending&sortBy=SortName&collapseBoxSetItems=false")]
    Task<JellyfinLibraryItemsResponse> GetCollectionItems(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string parentId,
        [Query]
        string fields = "Etag",
        [Query]
        string includeItemTypes = "Movie,Series,Season,Episode",
        [Query]
        bool recursive = true,
        [Query]
        int startIndex = 0,
        [Query]
        int limit = 0);

    [Get("/Items/{itemId}/PlaybackInfo")]
    Task<JellyfinPlaybackInfoResponse> GetPlaybackInfo(
        [Header("Authorization")]
        string authorizationHeader,
        string itemId);

    [Get("/Search/Hints")]
    Task<JellyfinSearchHintsResponse> SearchHints(
        [Header("Authorization")]
        string authorizationHeader,
        [Query]
        string searchTerm,
        [Query]
        string includeItemTypes = "Series",
        [Query]
        string parentId = null,
        [Query]
        int limit = 20);
}
