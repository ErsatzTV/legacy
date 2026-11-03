using System.Globalization;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Infrastructure;
using ErsatzTV.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Playouts;

[TestFixture]
public class NextPlayoutDependentsTests
{
    private SqliteConnection _connection;
    private TvContext _dbContext;
    private int _profileId;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:;foreign keys=true;");
        await _connection.OpenAsync();

        DbContextOptions<TvContext> options = new DbContextOptionsBuilder<TvContext>()
            .UseSqlite(_connection, o => o.MigrationsAssembly("ErsatzTV.Infrastructure.Sqlite"))
            .Options;

        _dbContext = new TvContext(
            options,
            NullLoggerFactory.Instance,
            new SlowQueryInterceptor(NullLogger<SlowQueryInterceptor>.Instance));
        await _dbContext.Database.MigrateAsync();

        var resolution = new Resolution { Name = "1080p", Width = 1920, Height = 1080 };
        _dbContext.Resolutions.Add(resolution);
        var profile = new FFmpegProfile { Name = "Profile", Resolution = resolution };
        _dbContext.FFmpegProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        _profileId = profile.Id;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task ForWatermark_Should_Find_Channel_Item_Default_Deco_And_Template_Deco_Users()
    {
        ChannelWatermark watermark = AddWatermark("wm");
        ChannelWatermark other = AddWatermark("other");

        Channel channelWatermark = AddChannel("1");
        channelWatermark.Watermark = watermark;

        Channel itemWatermark = AddChannel("2");
        Playout itemPlayout = AddPlayout(itemWatermark);
        PlayoutItem item = AddPlayoutItem(itemPlayout, AddMovie(AddLibraryPath()));
        item.Watermarks = [watermark];

        Channel defaultDeco = AddChannel("3");
        AddPlayout(defaultDeco).Deco = AddDeco("default", watermark);

        Channel templateDeco = AddChannel("4");
        AddPlayoutTemplate(AddPlayout(templateDeco), AddDecoTemplate(AddDeco("template", watermark)));

        Channel mirror = AddChannel("5");
        mirror.PlayoutSource = ChannelPlayoutSource.Mirror;
        mirror.MirrorSourceChannel = defaultDeco;

        Channel unrelated = AddChannel("6");
        unrelated.Watermark = other;
        AddPlayout(unrelated).Deco = AddDeco("unrelated", other);

        await _dbContext.SaveChangesAsync();

        List<string> result = await NextPlayoutDependents.ForWatermark(_dbContext, watermark.Id, CancellationToken.None);

        result.ShouldBe(["1", "2", "3", "4", "5"], ignoreOrder: true);
    }

    [Test]
    public async Task ForWatermark_Should_Find_All_Channels_For_Global_Watermark()
    {
        ChannelWatermark watermark = AddWatermark("global");
        AddChannel("1");
        AddChannel("2");
        await _dbContext.SaveChangesAsync();

        _dbContext.ConfigElements.Add(
            new ConfigElement
            {
                Key = ConfigElementKey.FFmpegGlobalWatermarkId.Key,
                Value = watermark.Id.ToString(CultureInfo.InvariantCulture)
            });
        await _dbContext.SaveChangesAsync();

        List<string> result = await NextPlayoutDependents.ForWatermark(_dbContext, watermark.Id, CancellationToken.None);

        result.ShouldBe(["1", "2"], ignoreOrder: true);
    }

    [Test]
    public async Task ForDeco_Should_Find_Default_And_Template_Deco_Users()
    {
        Deco deco = AddDeco("deco");

        Channel defaultDeco = AddChannel("1");
        AddPlayout(defaultDeco).Deco = deco;

        Channel templateDeco = AddChannel("2");
        AddPlayoutTemplate(AddPlayout(templateDeco), AddDecoTemplate(deco));

        Channel unrelated = AddChannel("3");
        AddPlayout(unrelated).Deco = AddDeco("other");

        await _dbContext.SaveChangesAsync();

        List<string> result = await NextPlayoutDependents.ForDeco(_dbContext, deco.Id, CancellationToken.None);

        result.ShouldBe(["1", "2"], ignoreOrder: true);
    }

    [Test]
    public async Task ForMediaSource_Should_Find_Channels_Playing_Its_Items()
    {
        LibraryPath libraryPath = AddLibraryPath();
        LibraryPath otherLibraryPath = AddLibraryPath();

        Channel channel = AddChannel("1");
        Playout playout = AddPlayout(channel);
        AddPlayoutItem(playout, AddMovie(libraryPath));
        AddPlayoutItem(playout, AddMovie(libraryPath));

        Channel unrelated = AddChannel("2");
        AddPlayoutItem(AddPlayout(unrelated), AddMovie(otherLibraryPath));

        await _dbContext.SaveChangesAsync();

        List<string> result = await NextPlayoutDependents.ForMediaSource(
            _dbContext,
            libraryPath.Library.MediaSourceId,
            CancellationToken.None);

        result.ShouldBe(["1"]);
    }

    private Channel AddChannel(string number)
    {
        var channel = new Channel(Guid.NewGuid())
        {
            Number = number,
            Name = number,
            FFmpegProfileId = _profileId,
            StreamingMode = StreamingMode.TransportStreamHybrid
        };
        _dbContext.Channels.Add(channel);
        return channel;
    }

    private Playout AddPlayout(Channel channel)
    {
        var playout = new Playout { Channel = channel, ScheduleKind = PlayoutScheduleKind.Block };
        _dbContext.Playouts.Add(playout);
        return playout;
    }

    private ChannelWatermark AddWatermark(string name)
    {
        var watermark = new ChannelWatermark { Name = name };
        _dbContext.ChannelWatermarks.Add(watermark);
        return watermark;
    }

    private Deco AddDeco(string name, params ChannelWatermark[] watermarks)
    {
        var deco = new Deco
        {
            Name = name,
            DecoGroup = new DecoGroup { Name = name },
            WatermarkMode = watermarks.Length > 0 ? DecoMode.Override : DecoMode.Inherit,
            Watermarks = watermarks.ToList()
        };
        _dbContext.Decos.Add(deco);
        return deco;
    }

    private DecoTemplate AddDecoTemplate(Deco deco)
    {
        var decoTemplate = new DecoTemplate
        {
            Name = deco.Name,
            DecoTemplateGroup = new DecoTemplateGroup { Name = deco.Name },
            Items = [new DecoTemplateItem { Deco = deco, StartTime = TimeSpan.Zero, EndTime = TimeSpan.Zero }]
        };
        _dbContext.DecoTemplates.Add(decoTemplate);
        return decoTemplate;
    }

    private void AddPlayoutTemplate(Playout playout, DecoTemplate decoTemplate) =>
        _dbContext.PlayoutTemplates.Add(
            new PlayoutTemplate
            {
                Playout = playout,
                Template = new Template
                {
                    Name = decoTemplate.Name,
                    TemplateGroup = new TemplateGroup { Name = decoTemplate.Name }
                },
                DecoTemplate = decoTemplate,
                DaysOfWeek = [],
                DaysOfMonth = [],
                MonthsOfYear = []
            });

    private LibraryPath AddLibraryPath()
    {
        var libraryPath = new LibraryPath
        {
            Path = Guid.NewGuid().ToString(),
            Library = new LocalLibrary
            {
                Name = "Library",
                MediaKind = LibraryMediaKind.Movies,
                MediaSource = new LocalMediaSource()
            }
        };
        _dbContext.LibraryPaths.Add(libraryPath);
        return libraryPath;
    }

    private Movie AddMovie(LibraryPath libraryPath)
    {
        var movie = new Movie { LibraryPath = libraryPath };
        _dbContext.MediaItems.Add(movie);
        return movie;
    }

    private PlayoutItem AddPlayoutItem(Playout playout, MediaItem mediaItem)
    {
        var item = new PlayoutItem
        {
            Playout = playout,
            MediaItem = mediaItem,
            Start = DateTime.UtcNow,
            Finish = DateTime.UtcNow.AddHours(1)
        };
        _dbContext.PlayoutItems.Add(item);
        return item;
    }
}
