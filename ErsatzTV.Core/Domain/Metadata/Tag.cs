namespace ErsatzTV.Core.Domain;

public class Tag
{
    public static readonly string PlexNetworkTypeId = "319";
    public static readonly string NfoCountryTypeId = "nfo/country";

    // several scanners write tags for one metadata row
    // each scanner must remove only the rows that it owns
    public static readonly string PlexLabelTypeId = "plex/label";
    public static readonly string PlexCollectionTypeId = "plex/collection";
    public static readonly string EmbyCollectionTypeId = "emby/collection";
    public static readonly string JellyfinCollectionTypeId = "jellyfin/collection";

    public int Id { get; set; }
    public string Name { get; set; }
    public string ExternalCollectionId { get; set; }
    public string ExternalTypeId { get; set; }

    // a tag with a new type id must stay in the tag field, or a smart collection
    // that uses tag: loses items
    public static bool IsSearchTag(Tag tag) =>
        tag.ExternalTypeId != PlexNetworkTypeId && tag.ExternalTypeId != NfoCountryTypeId;
}
