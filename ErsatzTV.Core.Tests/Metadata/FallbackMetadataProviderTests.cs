using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Metadata;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Metadata;

[TestFixture]
public class FallbackMetadataProviderTests
{
    [SetUp]
    public void SetUp() => _fallbackMetadataProvider = new FallbackMetadataProvider();

    private FallbackMetadataProvider _fallbackMetadataProvider;

    // deep scan updates existing metadata with these; null collections throw
    [Test]
    public void GetFallbackMetadataForShow_Should_Initialize_Collections()
    {
        ShowMetadata metadata = _fallbackMetadataProvider.GetFallbackMetadataForShow(
            Path.Combine("tv", "Awesome Show (2021)"));

        metadata.Genres.ShouldNotBeNull();
        metadata.Tags.ShouldNotBeNull();
        metadata.Studios.ShouldNotBeNull();
        metadata.Actors.ShouldNotBeNull();
        metadata.Artwork.ShouldNotBeNull();
        metadata.Guids.ShouldNotBeNull();
    }

    [Test]
    public void GetFallbackMetadataForArtist_Should_Initialize_Collections()
    {
        ArtistMetadata metadata = _fallbackMetadataProvider.GetFallbackMetadataForArtist(
            Path.Combine("music videos", "Awesome Artist"));

        metadata.Artwork.ShouldNotBeNull();
        metadata.Genres.ShouldNotBeNull();
        metadata.Styles.ShouldNotBeNull();
        metadata.Moods.ShouldNotBeNull();
    }

    [Test]
    [TestCase("Awesome Show - s01e02.mkv", 1, 2)]
    [TestCase("Awesome Show - S01E02.mkv", 1, 2)]
    [TestCase("Awesome Show - s1e2.mkv", 1, 2)]
    [TestCase("Awesome Show - S1E2.mkv", 1, 2)]
    [TestCase("Awesome Show - s01e02 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show - S01E02 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show - s1e2 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show - S1E2 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show (2021) - s01e02 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show (2021) - S01E02 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show (2021) - s1e2 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show (2021) - S1E2 - Episode Title.mkv", 1, 2)]
    [TestCase("Awesome Show - s01e02 - Episode Title-720p.mkv", 1, 2)]
    [TestCase("Awesome Show - S01E02 - Episode Title-720p.mkv", 1, 2)]
    [TestCase("Awesome Show - s1e2 - Episode Title-720p.mkv", 1, 2)]
    [TestCase("Awesome Show - S1E2 - Episode Title-720p.mkv", 1, 2)]
    [TestCase(
        "Awesome Show (2021) - S01E02 - Description; More Description (1080p QUALITY codec GROUP).mkv",
        1,
        2)]
    [TestCase(
        "Awesome.Show.S01E02.Description.more.Description.QUAlity.codec.CODEC-GROUP.mkv",
        1,
        2)]
    [TestCase("Awesome Show - s01.e02.mkv", 1, 2)]
    [TestCase("Awesome Show - S01.E02.mkv", 1, 2)]
    [TestCase("Awesome Show - s01_e02.mkv", 1, 2)]
    [TestCase("Awesome Show - S01_E02.mkv", 1, 2)]
    [TestCase("Awesome Show - s01xe02.mkv", 1, 2)]
    [TestCase("Awesome Show - S01XE02.mkv", 1, 2)]
    [TestCase("Awesome Show - 1x02.mkv", 1, 2)]
    public void GetFallbackMetadata_ShouldHandleVariousFormats(string path, int season, int episode)
    {
        List<EpisodeMetadata> metadata = _fallbackMetadataProvider.GetFallbackMetadata(
            new Episode
            {
                LibraryPath = new LibraryPath(),
                MediaVersions = new List<MediaVersion>
                {
                    new()
                    {
                        MediaFiles = new List<MediaFile>
                        {
                            new() { Path = path }
                        }
                    }
                }
            });

        metadata.Count.ShouldBe(1);
        // TODO: how can we test season number? do we need to?
        // metadata.Season.ShouldBe(season);
        metadata.Head().EpisodeNumber.ShouldBe(episode);
    }

    [TestCase("Awesome Show - S01_BLAH.mkv", 0)]
    [TestCase("Awesome Show - NO_EPISODE_NUMBER_HERE.mkv", 0)]
    public void GetFallbackMetadata_ShouldHandleNonEpisodes(string path, int episode)
    {
        List<EpisodeMetadata> metadata = _fallbackMetadataProvider.GetFallbackMetadata(
            new Episode
            {
                LibraryPath = new LibraryPath(),
                MediaVersions = new List<MediaVersion>
                {
                    new()
                    {
                        MediaFiles = new List<MediaFile>
                        {
                            new() { Path = path }
                        }
                    }
                }
            });

        metadata.Count.ShouldBe(1);
        metadata.Head().EpisodeNumber.ShouldBe(episode);
    }

    [Test]
    [TestCase("Awesome Show - s01e02-s01e03.mkv", 1, 2, 3)]
    [TestCase("Awesome Show - s01e02-whatever-s01e03-whatever2.mkv", 1, 2, 3)]
    [TestCase("Awesome Show - s01e02e03.mkv", 1, 2, 3)]
    [TestCase("Awesome Show - s01e02-03.mkv", 1, 2, 3)]
    public void GetFallbackMetadata_Should_Handle_Two_Episode_Formats(
        string path,
        int season,
        int episode1,
        int episode2)
    {
        List<EpisodeMetadata> metadata = _fallbackMetadataProvider.GetFallbackMetadata(
            new Episode
            {
                LibraryPath = new LibraryPath(),
                MediaVersions = new List<MediaVersion>
                {
                    new()
                    {
                        MediaFiles = new List<MediaFile>
                        {
                            new() { Path = path }
                        }
                    }
                }
            });

        metadata.Count.ShouldBe(2);
        metadata.Map(m => m.EpisodeNumber).ShouldBe([episode1, episode2]);
    }

    [Test]
    [TestCase("Something (2021).mkv", "Something")]
    [TestCase("Something Else (2021).mkv", "Something Else")]
    public void GetFallbackMetadata_Should_Set_Proper_Movie_Title(string path, string expectedTitle)
    {
        MovieMetadata metadata = _fallbackMetadataProvider.GetFallbackMetadata(
            new Movie
            {
                LibraryPath = new LibraryPath(),
                MediaVersions = new List<MediaVersion>
                {
                    new()
                    {
                        MediaFiles = new List<MediaFile>
                        {
                            new() { Path = path }
                        }
                    }
                }
            });

        metadata.ShouldNotBeNull();
        metadata.Title.ShouldBe(expectedTitle);
    }

    [Test]
    [TestCase(@"/Whatever/American Dad! S01", 1)]
    [TestCase(@"/Whatever/Season 2", 2)]
    [TestCase(@"/Whatever/Season 02", 2)]
    [TestCase(@"/Whatever/Staffel 2", 2)]
    [TestCase(@"/Whatever/Staffel 02", 2)]
    [TestCase(@"/Whatever/Seinfeld/S02", 2)]
    [TestCase(@"/Whatever/Seinfeld/2", 2)]
    [TestCase(@"/Whatever/Season 2009", 2009)]
    [TestCase(@"/Whatever/Season1", 1)]
    [TestCase(@"/Bojack Horseman/Bojack.Horseman.S04.1080p.WEB.x264-ABBA", 4)]
    [TestCase(@"/Whatever/Season 7 (2016)", 7)]
    [TestCase(@"/Whatever/Season (8)", null)]
    [TestCase(@"/Whatever/s06e05", null)]
    [TestCase(@"/Whatever/The.Legend.of.Condor.Heroes.2017.V2.web-dl.1080p.h264.aac-hdctv", null)]
    [TestCase(@"/Whatever/extras", null)]
    [TestCase(@"/Whatever/specials", 0)]
    [TestCase(@"Stargate SG1 S08", 8)]
    public void GetSeasonNumberForFolder_ShouldHandleVariousFormats(string folder, int? season)
    {
        Option<int> actual = _fallbackMetadataProvider.GetSeasonNumberForFolder(folder);
        actual.ShouldBe(Optional(season));
    }
}
