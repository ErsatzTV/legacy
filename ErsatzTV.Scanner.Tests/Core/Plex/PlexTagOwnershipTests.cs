using ErsatzTV.Core.Domain;
using ErsatzTV.Scanner.Core.Plex;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Scanner.Tests.Core.Plex;

[TestFixture]
public class PlexTagOwnershipTests
{
    private static Tag Label(string name, string id) =>
        new() { Name = name, ExternalCollectionId = id, ExternalTypeId = Tag.PlexLabelTypeId };

    private static Tag Collection(string name, string key) =>
        new() { Name = name, ExternalCollectionId = key, ExternalTypeId = Tag.PlexCollectionTypeId };

    private static Tag Network(string name) =>
        new() { Name = name, ExternalTypeId = Tag.PlexNetworkTypeId };

    private static Tag Country(string name) =>
        new() { Name = name, ExternalTypeId = Tag.NfoCountryTypeId };

    private static Tag Untyped(string name) => new() { Name = name };

    [Test]
    public void TagsToRemove_Should_Keep_Tags_Of_Other_Scanners()
    {
        List<Tag> existing =
        [
            Collection("Marvel", "90210"),
            Network("HBO"),
            Country("USA")
        ];

        PlexTagOwnership.TagsToRemove(existing, []).ShouldBeEmpty();
    }

    [Test]
    public void TagsToRemove_Should_Remove_A_Label_That_Plex_No_Longer_Has()
    {
        List<Tag> existing = [Label("4K Remux", "5"), Collection("Marvel", "90210")];

        List<Tag> result = PlexTagOwnership.TagsToRemove(existing, []).ToList();

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("4K Remux");
    }

    [Test]
    public void TagsToRemove_Should_Keep_A_Label_That_Plex_Still_Has()
    {
        List<Tag> existing = [Label("4K Remux", "5")];
        List<Tag> incoming = [Label("4K Remux", "5")];

        PlexTagOwnership.TagsToRemove(existing, incoming).ShouldBeEmpty();
    }

    [Test]
    public void TagsToRemove_Should_Remove_An_Untyped_Label_From_An_Older_Version()
    {
        List<Tag> existing = [Untyped("4K Remux")];
        List<Tag> incoming = [Label("4K Remux", "5")];

        List<Tag> result = PlexTagOwnership.TagsToRemove(existing, incoming).ToList();

        result.Count.ShouldBe(1);
        result[0].ExternalTypeId.ShouldBeNull();
    }

    [Test]
    public void A_Renamed_Label_Should_Be_Removed_And_Added()
    {
        List<Tag> existing = [Label("4K Remux", "5")];
        List<Tag> incoming = [Label("4K Remaster", "5")];

        List<Tag> toRemove = PlexTagOwnership.TagsToRemove(existing, incoming).ToList();
        List<Tag> toAdd = PlexTagOwnership.TagsToAdd(existing, incoming).ToList();

        toRemove.Count.ShouldBe(1);
        toRemove[0].Name.ShouldBe("4K Remux");
        toAdd.Count.ShouldBe(1);
        toAdd[0].Name.ShouldBe("4K Remaster");
    }

    [Test]
    public void TagsToAdd_Should_Not_Repeat_A_Label_That_Exists()
    {
        List<Tag> existing = [Label("4K Remux", "5"), Network("HBO")];
        List<Tag> incoming = [Label("4K Remux", "5")];

        PlexTagOwnership.TagsToAdd(existing, incoming).ShouldBeEmpty();
    }

    [Test]
    public void TagsToAdd_Should_Add_A_Label_That_Shares_A_Name_With_A_Network()
    {
        List<Tag> existing = [Network("HBO")];
        List<Tag> incoming = [Label("HBO", "5")];

        List<Tag> result = PlexTagOwnership.TagsToAdd(existing, incoming).ToList();

        result.Count.ShouldBe(1);
        result[0].ExternalTypeId.ShouldBe(Tag.PlexLabelTypeId);
    }

    [Test]
    public void IsSearchTag_Should_Hold_Labels_And_Collections_But_Not_Networks()
    {
        Tag.IsSearchTag(Label("4K Remux", "5")).ShouldBeTrue();
        Tag.IsSearchTag(Collection("Marvel", "90210")).ShouldBeTrue();
        Tag.IsSearchTag(Untyped("local")).ShouldBeTrue();
        Tag.IsSearchTag(Network("HBO")).ShouldBeFalse();
        Tag.IsSearchTag(Country("USA")).ShouldBeFalse();
    }
}
