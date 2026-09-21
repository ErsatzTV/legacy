using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Libraries;
using NUnit.Framework;
using Shouldly;
using static ErsatzTV.Core.Libraries.LocalLibraryPaths;

namespace ErsatzTV.Core.Tests.Libraries;

[TestFixture]
public class LocalLibraryPathsTests
{
    [TestFixture]
    public class NormalizePath
    {
        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/movies", "/MEDIA/MOVIES")]
        [TestCase("/media/movies/", "/MEDIA/MOVIES")]
        [TestCase("/Volumes/Gregflix v2/Remote Streams", "/VOLUMES/GREGFLIX V2/REMOTE STREAMS")]
        [TestCase("/media/movies/../tv", "/MEDIA/TV")]
        [TestCase("/", "")]
        public void Should_Normalize_Unix_Paths(string input, string expected) =>
            LocalLibraryPaths.NormalizePath(input).ShouldBe(expected);

        [Test]
        [Platform("Win")]
        [TestCase(@"C:\Media\Movies", @"C:\MEDIA\MOVIES")]
        [TestCase(@"C:\Media\Movies\", @"C:\MEDIA\MOVIES")]
        [TestCase("C:/Media/Movies", @"C:\MEDIA\MOVIES")]
        [TestCase(@"\\nas\share\Media", @"\\NAS\SHARE\MEDIA")]
        [TestCase(@"\\nas\share\Media\", @"\\NAS\SHARE\MEDIA")]
        [TestCase("//nas/share/Media", @"\\NAS\SHARE\MEDIA")]
        public void Should_Normalize_Windows_Paths(string input, string expected) =>
            LocalLibraryPaths.NormalizePath(input).ShouldBe(expected);

        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/Movies", "/MEDIA/MOVIES/")]
        [TestCase("/media/movies", "/media/movies/")]
        [TestCase("/media/tv", "/media/movies/../tv")]
        public void Should_Treat_Equivalent_Unix_Paths_As_Equal(string one, string two) =>
            LocalLibraryPaths.NormalizePath(one).ShouldBe(LocalLibraryPaths.NormalizePath(two));

        [Test]
        [Platform("Win")]
        [TestCase(@"C:\Media", "C:/Media")]
        [TestCase(@"C:\Media", @"c:\media\")]
        [TestCase(@"\\nas\share\Media", @"\\NAS\share\media\")]
        [TestCase(@"\\nas\share\Media", "//nas/share/Media")]
        public void Should_Treat_Equivalent_Windows_Paths_As_Equal(string one, string two) =>
            LocalLibraryPaths.NormalizePath(one).ShouldBe(LocalLibraryPaths.NormalizePath(two));

        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/tv", "/media/tv2")]
        [TestCase("/media/tv", "/media/tv/kids")]
        public void Should_Treat_Different_Unix_Paths_As_Different(string one, string two) =>
            LocalLibraryPaths.NormalizePath(one).ShouldNotBe(LocalLibraryPaths.NormalizePath(two));

        [Test]
        [Platform(Exclude = "Win")]
        public void Should_Not_Percent_Decode() =>
            LocalLibraryPaths.NormalizePath("/media/100%20Movies")
                .ShouldNotBe(LocalLibraryPaths.NormalizePath("/media/100 Movies"));

        // unrooted paths could be stored before validation existed; they must not break later edits
        [Test]
        [TestCase("Volumes/Gregflix v2/ErsatzTV Filler/Remote Streams")]
        [TestCase("media/movies")]
        [TestCase("./media/movies")]
        [TestCase("~/media/movies")]
        [TestCase("\u00a0/media/movies")] // non-breaking space; option+space on macOS
        [TestCase("\u200b/media/movies")] // zero-width space
        [TestCase("\"/media/movies\"")]
        public void Should_Not_Throw_For_Unrooted_Or_Malformed_Input(string input) =>
            Should.NotThrow(() => LocalLibraryPaths.NormalizePath(input));

        [Test]
        [Platform("Win")]
        [TestCase(@"Media\Movies")]
        [TestCase(@".\Media\Movies")]
        [TestCase(@"\Media\Movies")] // relative to current drive
        [TestCase("C:Media")] // relative to current directory on drive C
        public void Should_Not_Throw_For_Unrooted_Or_Malformed_Windows_Input(string input) =>
            Should.NotThrow(() => LocalLibraryPaths.NormalizePath(input));

    }

    [TestFixture]
    public class IsFullyQualified
    {
        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/")]
        [TestCase("/media")]
        [TestCase("/media/movies/")]
        [TestCase("/Volumes/Gregflix v2/Remote Streams")]
        [TestCase("/media/100%20Movies")]
        [TestCase("/media/tv/../movies")]
        public void Should_Accept_Absolute_Unix_Paths(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeTrue();

        [Test]
        [Platform("Win")]
        [TestCase(@"C:\")]
        [TestCase(@"C:\Media")]
        [TestCase(@"C:\Media\Movies\")]
        [TestCase("C:/Media/Movies")]
        [TestCase(@"\\nas\share")]
        [TestCase(@"\\nas\share\Media")]
        [TestCase("//nas/share/Media")]
        [TestCase(@"\\?\C:\Media")]
        [TestCase(@"\\?\UNC\nas\share\Media")]
        public void Should_Accept_Absolute_Windows_Paths(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeTrue();

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\t")]
        public void Should_Reject_Empty_Input(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeFalse();

        [Test]
        [TestCase("Volumes/Gregflix v2/ErsatzTV Filler/Remote Streams")]
        [TestCase("media/movies")]
        [TestCase("./media/movies")]
        [TestCase("../media/movies")]
        [TestCase("~/media/movies")]
        [TestCase("~")]
        [TestCase(".")]
        public void Should_Reject_Relative_Paths(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeFalse();

        [Test]
        [TestCase(" /media/movies")]
        [TestCase("\u00a0/media/movies")] // non-breaking space; option+space on macOS
        [TestCase("\u200b/media/movies")] // zero-width space
        [TestCase("\ufeff/media/movies")] // byte order mark
        [TestCase("\"/media/movies\"")]
        [TestCase("'/media/movies'")]
        [TestCase("file:///media/movies")]
        [TestCase("smb://nas/share/media")]
        public void Should_Reject_Paths_With_Invalid_Prefix(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeFalse();

        // .NET resolves these relative to the current drive or directory
        [Test]
        [Platform("Win")]
        [TestCase(@"\Media\Movies")]
        [TestCase("/media/movies")]
        [TestCase("C:Media")]
        [TestCase(@"C:Media\Movies")]
        [TestCase(@"Media\Movies")]
        [TestCase(@".\Media\Movies")]
        [TestCase(@"\\nas")] // server with no share
        public void Should_Reject_Partially_Qualified_Windows_Paths(string path) =>
            LocalLibraryPaths.IsFullyQualified(path).ShouldBeFalse();
    }

    [TestFixture]
    public class AreSubPaths
    {
        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/tv", "/media/tv")]
        [TestCase("/media/tv", "/media/tv/")]
        [TestCase("/media/tv/", "/media/tv")]
        [TestCase("/media/tv", "/media/tv/kids")]
        [TestCase("/media/tv/kids", "/media/tv")]
        [TestCase("/media/TV", "/media/tv/Kids")]
        [TestCase("/Volumes/Gregflix v2/ErsatzTV Filler", "/Volumes/Gregflix v2/ErsatzTV Filler/Remote Streams")]
        public void Should_Detect_Unix_Sub_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeTrue();

        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/tv", "/media/tv2")]
        [TestCase("/media/tv", "/media/movies")]
        [TestCase("/media/tv/kids", "/media/tv/adults")]
        public void Should_Not_Detect_Unrelated_Unix_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeFalse();

        [Test]
        [Platform("Win")]
        [TestCase(@"D:\Media\TV", @"D:\Media\TV")]
        [TestCase(@"D:\Media\TV", @"d:\media\tv\kids")]
        [TestCase(@"\\nas\share\TV", @"\\nas\share\TV\Kids")]
        [TestCase(@"\\NAS\share\TV", @"\\nas\share\tv")]
        public void Should_Detect_Windows_Sub_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeTrue();

        [Test]
        [Platform("Win")]
        [TestCase(@"D:\Media\TV", @"D:\Media\TV2")]
        [TestCase(@"D:\Media\TV", @"E:\Media\TV")]
        [TestCase(@"\\nas\share\TV", @"\\nas\other\TV")]
        public void Should_Not_Detect_Unrelated_Windows_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeFalse();

        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("/media/tv", "/media/movies/../tv/kids")]
        [TestCase("/media/tv", "/media//tv")]
        public void Should_Detect_Unnormalized_Unix_Sub_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeTrue();

        [Test]
        [Platform("Win")]
        [TestCase(@"D:\Media\TV", "D:/Media/TV/Kids")]
        [TestCase("D:/Media/TV", @"D:\Media\TV")]
        [TestCase(@"\\nas\share\TV", "//nas/share/TV/Kids")]
        public void Should_Detect_Mixed_Separator_Windows_Sub_Paths(string one, string two) =>
            LocalLibraryPaths.AreSubPaths(one, two).ShouldBeTrue();
    }

    [TestFixture]
    public class Conflicts
    {
        [Test]
        [Platform(Exclude = "Win")]
        [TestCase(LibraryMediaKind.Movies, LibraryMediaKind.Movies)]
        [TestCase(LibraryMediaKind.Movies, LibraryMediaKind.Shows)]
        [TestCase(LibraryMediaKind.OtherVideos, LibraryMediaKind.RemoteStreams)]
        [TestCase(LibraryMediaKind.Images, LibraryMediaKind.Images)]
        public void Should_Conflict_For_Overlapping_Paths(LibraryMediaKind kind1, LibraryMediaKind kind2)
        {
            var one = new LocalPath(kind1, "/media/stuff");
            var two = new LocalPath(kind2, "/media/stuff/more");
            LocalLibraryPaths.Conflicts(one, two).ShouldBeTrue();
            LocalLibraryPaths.Conflicts(two, one).ShouldBeTrue();
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void Should_Not_Conflict_For_Images_And_Other_Videos()
        {
            var images = new LocalPath(LibraryMediaKind.Images, "/media/stuff");
            var otherVideos = new LocalPath(LibraryMediaKind.OtherVideos, "/media/stuff");
            LocalLibraryPaths.Conflicts(images, otherVideos).ShouldBeFalse();
            LocalLibraryPaths.Conflicts(otherVideos, images).ShouldBeFalse();
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void Should_Not_Conflict_For_Unrelated_Paths()
        {
            var one = new LocalPath(LibraryMediaKind.Movies, "/media/movies");
            var two = new LocalPath(LibraryMediaKind.Movies, "/media/tv");
            LocalLibraryPaths.Conflicts(one, two).ShouldBeFalse();
        }

        // a malformed path already in the database must not block saving other libraries
        [Test]
        [Platform(Exclude = "Win")]
        [TestCase("Volumes/Gregflix v2/ErsatzTV Filler/Remote Streams")]
        [TestCase("")]
        [TestCase("\u00a0/media/stuff")]
        public void Should_Not_Throw_For_Malformed_Existing_Path(string existing)
        {
            var one = new LocalPath(LibraryMediaKind.Movies, existing);
            var two = new LocalPath(LibraryMediaKind.Movies, "/media/stuff");
            Should.NotThrow(() => LocalLibraryPaths.Conflicts(one, two));
            Should.NotThrow(() => LocalLibraryPaths.Conflicts(two, one));
        }
    }
}
