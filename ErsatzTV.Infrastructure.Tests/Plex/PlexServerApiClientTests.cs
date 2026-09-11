using ErsatzTV.Infrastructure.Plex;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Plex;

[TestFixture]
public class PlexServerApiClientTests
{
    [TestCase(@"C:\media\other\Travel\Summer\clip.mkv", @"C:\media\other")]
    [TestCase("/media/other/Travel/Summer/clip.mkv", "/media/other")]
    [TestCase(@"\\server\media\other\Travel\Summer\clip.mkv", @"\\server\media\other")]
    [TestCase(@"C:\media\other/Travel\Summer/clip.mkv", "C:/media/other/")]
    [TestCase("/media/other/Travel/Summer/clip.mkv", "/media/other/")]
    [TestCase(@"C:\media\other\Travel\Summer\clip.mkv", @"c:\MEDIA\OTHER\")]
    public void FolderTags_Should_Include_Library_And_Each_Subfolder(string file, string libraryPath)
    {
        PlexServerApiClient.GetFolderTags(file, [libraryPath])
            .ShouldBe(["other", "Travel", "Summer"]);
    }

    [TestCase("/media/other/clip.mkv", "/media/other", new[] { "other" })]
    [TestCase(@"C:\media\other\clip.mkv", @"C:\media\other", new[] { "other" })]
    [TestCase("/Travel/clip.mkv", "/", new[] { "Travel" })]
    [TestCase(@"C:\Travel\clip.mkv", @"C:\", new[] { "Travel" })]
    [TestCase("/clip.mkv", "/", new string[0])]
    [TestCase(@"C:\clip.mkv", @"C:\", new string[0])]
    [TestCase("/media/other-extra/clip.mkv", "/media/other", new string[0])]
    [TestCase(@"C:\media\other-extra\clip.mkv", @"C:\media\other", new string[0])]
    [TestCase("clip.mkv", "/media/other", new string[0])]
    [TestCase("", "/media/other", new string[0])]
    [TestCase(null, "/media/other", new string[0])]
    public void FolderTags_Should_Handle_Roots_And_Require_Folder_Boundaries(
        string? file,
        string libraryPath,
        string[] expected)
    {
        PlexServerApiClient.GetFolderTags(file!, [libraryPath]).ShouldBe(expected);
    }

    [Test]
    public void FolderTags_Should_Use_First_Matching_Library_Path()
    {
        PlexServerApiClient.GetFolderTags(
                "/media/other-extra/Travel/clip.mkv",
                ["", "/media/other", "/media/other-extra", "/media/other-extra/Travel"])
            .ShouldBe(["other-extra", "Travel"]);
    }
}
