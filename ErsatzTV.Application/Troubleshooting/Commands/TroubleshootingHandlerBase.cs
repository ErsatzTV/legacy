using System.IO.Abstractions;
using Dapper;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Security;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Troubleshooting;

public abstract class TroubleshootingHandlerBase(
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    IFileSystem fileSystem)
    : NextChannelHandlerBase(fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem;

    protected static async Task<Validation<BaseError, MediaItem>> MediaItemMustExist(
        TvContext dbContext,
        int mediaItemId,
        CancellationToken cancellationToken) =>
        await dbContext.MediaItems
            .AsNoTracking()
            .Include(mi => mi.LibraryPath)
            .ThenInclude(mi => mi.Library)
            .Include(mi => (mi as Episode).EpisodeMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as Episode).Season)
            .ThenInclude(s => s.Show)
            .ThenInclude(s => s.ShowMetadata)
            .Include(mi => (mi as Movie).MovieMetadata)
            .ThenInclude(mm => mm.Subtitles)
            .Include(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Subtitles)
            .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artists)
            .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as MusicVideo).Artist)
            .ThenInclude(mv => mv.ArtistMetadata)
            .Include(mi => (mi as OtherVideo).OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Subtitles)
            .Include(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(ov => ov.MediaFiles)
            .Include(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(ov => ov.Streams)
            .Include(mi => (mi as Song).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Song).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as Song).SongMetadata)
            .ThenInclude(sm => sm.Artwork)
            .Include(mi => (mi as Image).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as Image).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as Image).ImageMetadata)
            .Include(mi => (mi as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mi => (mi as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mi => (mi as RemoteStream).RemoteStreamMetadata)
            .AsSplitQuery()
            .SingleOrDefaultAsync(mi => mi.Id == mediaItemId, cancellationToken)
            .Map(Optional)
            .Map(o => o.ToValidation<BaseError>(new UnableToLocatePlayoutItem()));

    protected static Task<Validation<BaseError, string>> FFmpegPathMustExist(
        TvContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken)
            .FilterT(File.Exists)
            .Map(maybePath => maybePath.ToValidation<BaseError>("FFmpeg path does not exist on filesystem"));

    protected Task<string> GetLocalPath(MediaItem mediaItem, CancellationToken cancellationToken) =>
        mediaItem.GetLocalPath(
            plexPathReplacementService,
            jellyfinPathReplacementService,
            embyPathReplacementService,
            cancellationToken);

    protected async Task<string> GetMediaItemPath(
        TvContext dbContext,
        MediaItem mediaItem,
        CancellationToken cancellationToken)
    {
        string path = await GetLocalPath(mediaItem, cancellationToken);

        DateTimeOffset exp = DateTimeOffset.Now + TimeSpan.FromMinutes(15);

        // check filesystem first
        if (_fileSystem.File.Exists(path))
        {
            if (mediaItem is RemoteStream remoteStream)
            {
                string sig = InternalUrlSigner.Sign(
                    exp,
                    "remote-stream",
                    $"{remoteStream.Id}");

                path = !string.IsNullOrWhiteSpace(remoteStream.Url)
                    ? remoteStream.Url
                    : $"http://localhost:{Settings.StreamingPort}/internal/ffmpeg/remote-stream/{remoteStream.Id}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
            }

            return path;
        }

        // attempt to remotely stream plex
        MediaFile file = mediaItem.GetHeadVersion().MediaFiles.Head();
        switch (file)
        {
            case PlexMediaFile pmf:
                Option<int> maybeId = await dbContext.Connection.QuerySingleOrDefaultAsync<int>(
                        @"SELECT PMS.Id FROM PlexMediaSource PMS
                  INNER JOIN Library L on PMS.Id = L.MediaSourceId
                  INNER JOIN LibraryPath LP on L.Id = LP.LibraryId
                  WHERE LP.Id = @LibraryPathId",
                        new { mediaItem.LibraryPathId })
                    .Map(Optional);

                foreach (int plexMediaSourceId in maybeId)
                {
                    string sig = InternalUrlSigner.Sign(
                        exp,
                        "plex",
                        $"{plexMediaSourceId}",
                        $"{pmf.Key}");

                    return $"http://localhost:{Settings.StreamingPort}/internal/media/plex/{plexMediaSourceId}/{pmf.Key}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
                }

                break;
        }

        // attempt to remotely stream jellyfin
        Option<string> jellyfinItemId = mediaItem switch
        {
            JellyfinEpisode e => e.ItemId,
            JellyfinMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in jellyfinItemId)
        {
            string sig = InternalUrlSigner.Sign(exp, "jellyfin", $"{itemId}");
            return $"http://localhost:{Settings.StreamingPort}/internal/media/jellyfin/{itemId}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
        }

        // attempt to remotely stream emby
        Option<string> embyItemId = mediaItem switch
        {
            EmbyEpisode e => e.ItemId,
            EmbyMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in embyItemId)
        {
            string sig = InternalUrlSigner.Sign(exp, "emby", $"{itemId}");
            return $"http://localhost:{Settings.StreamingPort}/internal/media/emby/{itemId}?exp={exp.ToUnixTimeSeconds()}&sig={sig}";
        }

        return null;
    }
}
