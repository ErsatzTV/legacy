using System.Globalization;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Metadata;
using ErsatzTV.Scanner.Tests.Core.Fakes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace ErsatzTV.Scanner.Tests.Core.Metadata;

[TestFixture]
public class LocalSubtitlesProviderTests
{
    // test cases are from plex's example folder layout here
    // https://support.plex.tv/articles/200471133-adding-local-subtitles-to-your-media/
    // /Movies
    //    /Avatar (2009)
    //       Avatar (2009).mkv
    //       Avatar (2009).eng.srt
    //       Avatar (2009).en.forced.ass
    //       Avatar (2009).en.sdh.srt
    //       Avatar (2009).de.srt
    //       Avatar (2009).de.sdh.forced.srt

    [Test]
    public void Should_Find_All_Languages_Codecs_And_Flags_With_Full_Paths()
    {
        // normally this will have a full list from the database, but we just need these two for testing
        var cultures = new List<CultureInfo>
        {
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("de-DE")
        };

        var fakeFiles = new List<FakeFileEntry>
        {
            new(@"/Movies/Avatar (2009)/Avatar (2009).mkv"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).eng.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).en.forced.ass"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).en.sdh.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).de.srt"),

            // non-uniform (lower-case) extensions should also work
            new(@"/Movies/Avatar (2009)/Avatar (2009).DE.SDH.FORCED.SRT")
        };

        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Linux));
        IFileSystemInitializer<MockFileSystem> init = fileSystem.Initialize();
        foreach (var file in fakeFiles)
        {
            init.WithFile(file.Path);
        }

        var provider = new LocalSubtitlesProvider(
            Substitute.For<IMediaItemRepository>(),
            Substitute.For<IMetadataRepository>(),
            fileSystem,
            new LocalFileSystem(fileSystem, Substitute.For<ILogger<LocalFileSystem>>()),
            Substitute.For<ILogger<LocalSubtitlesProvider>>());

        List<Subtitle> result = provider.LocateExternalSubtitles(
            cultures,
            @"/Movies/Avatar (2009)/Avatar (2009).mkv",
            true);

        result.Count.ShouldBe(5);
        result.Count(s => s.Language == "eng").ShouldBe(3);
        result.Count(s => s.Language == "deu").ShouldBe(2);
        result.Count(s => s.Forced).ShouldBe(2);
        result.Count(s => s.SDH).ShouldBe(2);
        result.Count(s => s.Codec == "subrip").ShouldBe(4);
        result.Count(s => s.Codec == "ass").ShouldBe(1);

        result.All(s => s.Path.Contains("/Movies/Avatar (2009)")).ShouldBeTrue();
    }

    [Test]
    public void Should_Find_All_Languages_Codecs_And_Flags_With_File_Names()
    {
        // normally this will have a full list from the database, but we just need these two for testing
        var cultures = new List<CultureInfo>
        {
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("de-DE")
        };

        var fakeFiles = new List<FakeFileEntry>
        {
            new(@"/Movies/Avatar (2009)/Avatar (2009).mkv"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).eng.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).en.forced.ass"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).forced.en.ass"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).en.sdh.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).sdh.en.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).de.srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).srt"),
            new(@"/Movies/Avatar (2009)/Avatar (2009).forced.srt"),

            // non-uniform (lower-case) extensions should also work
            new(@"/Movies/Avatar (2009)/Avatar (2009).DE.SDH.FORCED.SRT")
        };

        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Linux));
        IFileSystemInitializer<MockFileSystem> init = fileSystem.Initialize();
        foreach (var file in fakeFiles)
        {
            init.WithFile(file.Path);
        }

        var provider = new LocalSubtitlesProvider(
            Substitute.For<IMediaItemRepository>(),
            Substitute.For<IMetadataRepository>(),
            fileSystem,
            new LocalFileSystem(fileSystem, Substitute.For<ILogger<LocalFileSystem>>()),
            Substitute.For<ILogger<LocalSubtitlesProvider>>());

        List<Subtitle> result = provider.LocateExternalSubtitles(
            cultures,
            @"/Movies/Avatar (2009)/Avatar (2009).mkv",
            false);

        result.Count.ShouldBe(9);
        result.Count(s => s.Language == "eng").ShouldBe(5);
        result.Count(s => s.Language == "deu").ShouldBe(2);
        result.Count(s => s.Language == "und").ShouldBe(2);
        result.Count(s => s.Forced).ShouldBe(4);
        result.Count(s => s.SDH).ShouldBe(3);
        result.Count(s => s.Codec == "subrip").ShouldBe(7);
        result.Count(s => s.Codec == "ass").ShouldBe(2);

        result.Count(s => s.Path.Contains("/Movies/Avatar (2009)")).ShouldBe(0);
    }

    [Test]
    public void Should_Not_Mark_Forced_From_Movie_title()
    {
        // normally this will have a full list from the database, but we just need these two for testing
        var cultures = new List<CultureInfo>
        {
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("de-DE")
        };

        var fakeFiles = new List<FakeFileEntry>
        {
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.mkv"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.eng.srt"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.en.forced.ass"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.forced.en.ass"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.en.sdh.srt"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.sdh.en.srt"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.de.srt"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.srt"),
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.forced.srt"),

            // non-uniform (lower-case) extensions should also work
            new(@"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.DE.SDH.FORCED.SRT")
        };

        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Linux));
        IFileSystemInitializer<MockFileSystem> init = fileSystem.Initialize();
        foreach (var file in fakeFiles)
        {
            init.WithFile(file.Path);
        }

        var provider = new LocalSubtitlesProvider(
            Substitute.For<IMediaItemRepository>(),
            Substitute.For<IMetadataRepository>(),
            fileSystem,
            new LocalFileSystem(fileSystem, Substitute.For<ILogger<LocalFileSystem>>()),
            Substitute.For<ILogger<LocalSubtitlesProvider>>());

        List<Subtitle> result = provider.LocateExternalSubtitles(
            cultures,
            @"/Movies/Bloodfist.III.Forced.To.Fight.1991/Bloodfist.III.Forced.To.Fight.1991.mkv",
            false);

        result.Count.ShouldBe(9);
        result.Count(s => s.Language == "eng").ShouldBe(5);
        result.Count(s => s.Language == "deu").ShouldBe(2);
        result.Count(s => s.Language == "und").ShouldBe(2);
        result.Count(s => s.Forced).ShouldBe(4);
        result.Count(s => s.SDH).ShouldBe(3);
        result.Count(s => s.Codec == "subrip").ShouldBe(7);
        result.Count(s => s.Codec == "ass").ShouldBe(2);

        result.Count(s => s.Path.Contains("/Movies/Bloodfist.III.Forced.To.Fight.1991")).ShouldBe(0);
    }
}
