using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure.Epg;
using ErsatzTV.Infrastructure.Epg.Models;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class TemplateDataRepository(IFileSystem fileSystem, IDbContextFactory<TvContext> dbContextFactory)
    : ITemplateDataRepository
{
    public async Task<Option<Dictionary<string, object>>> GetMediaItemTemplateData(
        MediaItem mediaItem,
        CancellationToken cancellationToken) =>
        mediaItem switch
        {
            Movie => await GetMovieTemplateData(mediaItem.Id, cancellationToken),
            Episode => await GetEpisodeTemplateData(mediaItem.Id, cancellationToken),
            MusicVideo => await GetMusicVideoTemplateData(mediaItem.Id, cancellationToken),
            OtherVideo => await GetOtherVideoTemplateData(mediaItem.Id, cancellationToken),
            _ => Option<Dictionary<string, object>>.None
        };

    public async Task<Option<Dictionary<string, object>>> GetEpgTemplateData(
        string channelNumber,
        DateTimeOffset time,
        int count)
    {
        try
        {
            if (count <= 0)
            {
                return Option<Dictionary<string, object>>.None;
            }

            if (channelNumber.Equals(
                    FileSystemLayout.TranscodeTroubleshootingChannel,
                    StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.Now;
                var topOfHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);

                List<EpgProgrammeTemplateData> result = [];

                for (var i = 0; i < count; i++)
                {
                    var data = new EpgProgrammeTemplateData
                    {
                        Title = $"Fake Epg Title {i}",
                        SubTitle = $"Fake Epg SubTitle {i}",
                        Description = string.Empty,
                        Rating = string.Empty,
                        Categories = [],
                        Date = $"Fake Epg Date {i}",
                        Start = topOfHour + i * TimeSpan.FromHours(1),
                        Stop = topOfHour + (i + 1) * TimeSpan.FromHours(1),
                    };

                    result.Add(data);
                }

                return new Dictionary<string, object>
                {
                    [EpgTemplateDataKey.Epg] = result
                };
            }

            string targetFile = Path.Combine(FileSystemLayout.ChannelGuideCacheFolder, $"{channelNumber}.xml");
            if (fileSystem.File.Exists(targetFile))
            {
                await using FileSystemStream stream = fileSystem.File.OpenRead(targetFile);
                List<EpgProgramme> xmlProgrammes = EpgReader.FindProgrammesAt(stream, time, count);
                var result = new List<Dictionary<string, object>>();

                foreach (EpgProgramme epgProgramme in xmlProgrammes)
                {
                    Dictionary<string, object> data = new()
                    {
                        ["Title"] = epgProgramme.Title?.Value,
                        ["SubTitle"] = epgProgramme.SubTitle?.Value,
                        ["Description"] = epgProgramme.Description?.Value,
                        ["Rating"] = epgProgramme.Rating?.Value,
                        ["Categories"] = (epgProgramme.Categories ?? []).Map(c => c.Value).ToArray(),
                        ["Date"] = epgProgramme.Date?.Value
                    };

                    if (epgProgramme.OtherElements?.Length > 0)
                    {
                        foreach (var otherElement in epgProgramme.OtherElements.Where(e =>
                                     e.NamespaceURI == EpgReader.XmlTvCustomNamespace))
                        {
                            data[otherElement.LocalName] = otherElement.InnerText;
                        }
                    }

                    if (DateTimeOffset.TryParseExact(
                            epgProgramme.Start,
                            EpgReader.XmlTvDateFormat,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTimeOffset start))
                    {
                        data["Start"] = start;
                    }

                    if (DateTimeOffset.TryParseExact(
                            epgProgramme.Stop,
                            EpgReader.XmlTvDateFormat,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTimeOffset stop))
                    {
                        data["Stop"] = stop;
                    }

                    result.Add(data);
                }

                return new Dictionary<string, object>
                {
                    [EpgTemplateDataKey.Epg] = result
                };
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        return Option<Dictionary<string, object>>.None;
    }

    private async Task<Option<Dictionary<string, object>>> GetMovieTemplateData(
        int movieId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Movie> maybeMovie = await dbContext.Movies
            .AsNoTracking()
            .Include(m => m.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(m => m.MovieMetadata)
            .ThenInclude(mm => mm.Studios)
            .Include(m => m.MovieMetadata)
            .ThenInclude(mm => mm.Directors)
            .Include(m => m.MovieMetadata)
            .ThenInclude(mm => mm.Genres)
            .SelectOneAsync(m => m.Id, m => m.Id == movieId, cancellationToken);

        foreach (Movie movie in maybeMovie)
        {
            foreach (MovieMetadata metadata in movie.MovieMetadata.HeadOrNone())
            {
                var headVersion = movie.GetHeadVersion();

                var result = new Dictionary<string, object>
                {
                    [MediaItemTemplateDataKey.Title] = metadata.Title,
                    [MediaItemTemplateDataKey.Plot] = metadata.Plot,
                    [MediaItemTemplateDataKey.ReleaseDate] = metadata.ReleaseDate,
                    [MediaItemTemplateDataKey.Studios] = (metadata.Studios ?? []).Map(s => s.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Directors] =
                        (metadata.Directors ?? []).Map(d => d.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Genres] = (metadata.Genres ?? []).Map(g => g.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Resolution] = new Resolution
                        { Height = headVersion.Height, Width = headVersion.Width },
                    [MediaItemTemplateDataKey.Duration] = headVersion.Duration,
                    [MediaItemTemplateDataKey.ContentRating] = metadata.ContentRating
                };

                foreach (var version in movie.MediaVersions.HeadOrNone())
                {
                    foreach (var file in version.MediaFiles.HeadOrNone())
                    {
                        result.Add(MediaItemTemplateDataKey.Path, file.Path);
                    }
                }

                return result;
            }
        }

        return Option<Dictionary<string, object>>.None;
    }

    private async Task<Option<Dictionary<string, object>>> GetEpisodeTemplateData(
        int episodeId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Episode> maybeEpisode = await dbContext.Episodes
            .AsNoTracking()
            .Include(e => e.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(e => e.Season)
            .ThenInclude(s => s.Show)
            .ThenInclude(s => s.ShowMetadata)
            .Include(e => e.EpisodeMetadata)
            .ThenInclude(em => em.Studios)
            .Include(e => e.EpisodeMetadata)
            .ThenInclude(em => em.Directors)
            .Include(e => e.EpisodeMetadata)
            .ThenInclude(em => em.Genres)
            .SelectOneAsync(e => e.Id, e => e.Id == episodeId, cancellationToken);

        var result = new Dictionary<string, object>();

        foreach (Episode episode in maybeEpisode)
        {
            foreach (ShowMetadata showMetadata in Optional(episode.Season?.Show?.ShowMetadata.HeadOrNone()).Flatten())
            {
                result.Add(MediaItemTemplateDataKey.ShowTitle, showMetadata.Title);
                result.Add(MediaItemTemplateDataKey.ShowYear, showMetadata.Year);
                result.Add(MediaItemTemplateDataKey.ShowContentRating, showMetadata.ContentRating);
                result.Add(
                    MediaItemTemplateDataKey.ShowGenres,
                    (showMetadata.Genres ?? []).Map(s => s.Name).OrderBy(identity));
            }

            var headVersion = episode.GetHeadVersion();

            foreach (EpisodeMetadata metadata in episode.EpisodeMetadata.HeadOrNone())
            {
                result.Add(MediaItemTemplateDataKey.Title, metadata.Title);
                result.Add(MediaItemTemplateDataKey.Plot, metadata.Plot);
                result.Add(MediaItemTemplateDataKey.ReleaseDate, metadata.ReleaseDate);
                result.Add(
                    MediaItemTemplateDataKey.Studios,
                    (metadata.Studios ?? []).Map(s => s.Name).OrderBy(identity));
                result.Add(
                    MediaItemTemplateDataKey.Directors,
                    (metadata.Directors ?? []).Map(s => s.Name).OrderBy(identity));
                result.Add(
                    MediaItemTemplateDataKey.Genres,
                    (metadata.Genres ?? []).Map(s => s.Name).OrderBy(identity));
                result.Add(
                    MediaItemTemplateDataKey.Resolution,
                    new Resolution { Height = headVersion.Height, Width = headVersion.Width });
                result.Add(MediaItemTemplateDataKey.Duration, headVersion.Duration);
            }

            foreach (var version in episode.MediaVersions.HeadOrNone())
            {
                foreach (var file in version.MediaFiles.HeadOrNone())
                {
                    result.Add(MediaItemTemplateDataKey.Path, file.Path);
                }
            }

            return result;
        }

        return Option<Dictionary<string, object>>.None;
    }

    private async Task<Option<Dictionary<string, object>>> GetMusicVideoTemplateData(
        int musicVideoId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<MusicVideo> maybeMusicVideo = await dbContext.MusicVideos
            .AsNoTracking()
            .Include(mv => mv.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mv => mv.Artist)
            .ThenInclude(a => a.ArtistMetadata)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artists)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Genres)
            .SelectOneAsync(mv => mv.Id, mv => mv.Id == musicVideoId, cancellationToken);

        foreach (MusicVideo musicVideo in maybeMusicVideo)
        {
            foreach (MusicVideoMetadata metadata in musicVideo.MusicVideoMetadata.HeadOrNone())
            {
                string artist = string.Empty;
                foreach (ArtistMetadata artistMetadata in Optional(musicVideo.Artist?.ArtistMetadata).Flatten())
                {
                    artist = artistMetadata.Title;
                }

                var headVersion = musicVideo.GetHeadVersion();

                var result = new Dictionary<string, object>
                {
                    [MediaItemTemplateDataKey.Title] = metadata.Title,
                    [MediaItemTemplateDataKey.Track] = metadata.Track,
                    [MediaItemTemplateDataKey.Album] = metadata.Album,
                    [MediaItemTemplateDataKey.Plot] = metadata.Plot,
                    [MediaItemTemplateDataKey.ReleaseDate] = metadata.ReleaseDate,
                    [MediaItemTemplateDataKey.Artists] = (metadata.Artists ?? []).Map(a => a.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Artist] = artist,
                    [MediaItemTemplateDataKey.Studios] = (metadata.Studios ?? []).Map(s => s.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Directors] =
                        (metadata.Directors ?? []).Map(d => d.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Genres] = (metadata.Genres ?? []).Map(g => g.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Resolution] = new Resolution
                        { Height = headVersion.Height, Width = headVersion.Width },
                    [MediaItemTemplateDataKey.Duration] = headVersion.Duration
                };

                foreach (var version in musicVideo.MediaVersions.HeadOrNone())
                {
                    foreach (var file in version.MediaFiles.HeadOrNone())
                    {
                        result.Add(MediaItemTemplateDataKey.Path, file.Path);
                    }
                }

                return result;
            }
        }

        return Option<Dictionary<string, object>>.None;
    }

    private async Task<Option<Dictionary<string, object>>> GetOtherVideoTemplateData(
        int otherVideoId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<OtherVideo> maybeOtherVideo = await dbContext.OtherVideos
            .AsNoTracking()
            .Include(mv => mv.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mv => mv.OtherVideoMetadata)
            .Include(mv => mv.OtherVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(mv => mv.OtherVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(mv => mv.OtherVideoMetadata)
            .ThenInclude(mvm => mvm.Genres)
            .SelectOneAsync(mv => mv.Id, mv => mv.Id == otherVideoId, cancellationToken);

        foreach (OtherVideo otherVideo in maybeOtherVideo)
        {
            foreach (OtherVideoMetadata metadata in otherVideo.OtherVideoMetadata.HeadOrNone())
            {
                var headVersion = otherVideo.GetHeadVersion();

                var result = new Dictionary<string, object>
                {
                    [MediaItemTemplateDataKey.Title] = metadata.Title,
                    [MediaItemTemplateDataKey.Plot] = metadata.Plot,
                    [MediaItemTemplateDataKey.ReleaseDate] = metadata.ReleaseDate,
                    [MediaItemTemplateDataKey.Studios] = (metadata.Studios ?? []).Map(s => s.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Directors] =
                        (metadata.Directors ?? []).Map(d => d.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Genres] = (metadata.Genres ?? []).Map(g => g.Name).OrderBy(identity),
                    [MediaItemTemplateDataKey.Resolution] = new Resolution
                        { Height = headVersion.Height, Width = headVersion.Width },
                    [MediaItemTemplateDataKey.Duration] = headVersion.Duration
                };

                foreach (var version in otherVideo.MediaVersions.HeadOrNone())
                {
                    foreach (var file in version.MediaFiles.HeadOrNone())
                    {
                        result.Add(MediaItemTemplateDataKey.Path, file.Path);
                    }
                }

                return result;
            }
        }

        return Option<Dictionary<string, object>>.None;
    }
}
