using ErsatzTV.Core.Domain;

namespace ErsatzTV.Scanner.Core.Plex;

internal static class PlexTagOwnership
{
    // an untyped row is a label that an older version wrote before labels had a type id
    public static bool IsLibraryScannerOwned(Tag tag) =>
        tag.ExternalTypeId == Tag.PlexLabelTypeId ||
        (tag.ExternalTypeId is null && tag.ExternalCollectionId is null);

    // a rename keeps the plex id, so the name is part of the identity; without the name
    // a renamed label never goes away and never comes back
    public static bool IsSameLabel(Tag left, Tag right) =>
        left.ExternalCollectionId == right.ExternalCollectionId && left.Name == right.Name;

    public static IEnumerable<Tag> TagsToRemove(IEnumerable<Tag> existing, IEnumerable<Tag> incoming)
    {
        var incomingLabels = incoming.ToList();
        return existing
            .Filter(IsLibraryScannerOwned)
            .Filter(tag => incomingLabels.All(label => !IsSameLabel(tag, label)));
    }

    public static IEnumerable<Tag> TagsToAdd(IEnumerable<Tag> existing, IEnumerable<Tag> incoming)
    {
        var existingTags = existing.ToList();
        return incoming.Filter(label => existingTags.All(tag => !IsSameLabel(label, tag)));
    }
}
