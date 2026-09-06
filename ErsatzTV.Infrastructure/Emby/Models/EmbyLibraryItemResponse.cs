namespace ErsatzTV.Infrastructure.Emby.Models;

public class EmbyLibraryItemResponse
{
    public string Name { get; set; }
    public string Id { get; set; }
    public string Etag { get; set; }
    public string Path { get; set; }
    public string OfficialRating { get; set; }
    public DateTimeOffset DateCreated { get; set; }
    public long RunTimeTicks { get; set; }
    public List<string> Genres { get; set; }
    public List<EmbyTagItemResponse> TagItems { get; set; }
    public int ProductionYear { get; set; }
    public EmbyProviderIdsResponse ProviderIds { get; set; }
    public string PremiereDate { get; set; }
    public List<EmbyMediaSourceResponse> MediaSources { get; set; }
    public string LocationType { get; set; }
    public string Overview { get; set; }
    public List<string> Taglines { get; set; }
    public List<EmbyStudioResponse> Studios { get; set; }
    public List<EmbyPersonResponse> People { get; set; }
    public EmbyImageTagsResponse ImageTags { get; set; }
    public List<string> BackdropImageTags { get; set; }
    public int? IndexNumber { get; set; }
    public string SeasonId { get; set; }
    public string Type { get; set; }
    public IList<EmbyChapterResponse> Chapters { get; set; }
}
