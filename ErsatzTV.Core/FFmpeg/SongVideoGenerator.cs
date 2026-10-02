using System.Globalization;
using System.Text;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.FFmpeg.State;

namespace ErsatzTV.Core.FFmpeg;

public class SongVideoGenerator : ISongVideoGenerator
{
    private readonly IFFmpegProcessService _ffmpegProcessService;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly IImageCache _imageCache;

    private readonly ITempFilePool _tempFilePool;

    public SongVideoGenerator(
        ITempFilePool tempFilePool,
        IImageCache imageCache,
        IFFmpegProcessService ffmpegProcessService,
        ILocalFileSystem localFileSystem)
    {
        _tempFilePool = tempFilePool;
        _imageCache = imageCache;
        _ffmpegProcessService = ffmpegProcessService;
        _localFileSystem = localFileSystem;
    }

    public async Task<Tuple<string, MediaVersion>> GenerateSongVideo(
        Song song,
        Channel channel,
        string ffmpegPath,
        string ffprobePath,
        int? seed,
        CancellationToken cancellationToken)
    {
        Option<string> subtitleFile = None;

        MediaVersion videoVersion = new FallbackMediaVersion
        {
            Id = -1,
            Chapters = [],
            Width = channel.FFmpegProfile.Resolution.Width / 10,
            Height = channel.FFmpegProfile.Resolution.Height / 10,
            SampleAspectRatio = "1:1",
            Streams = [new MediaStream { MediaStreamKind = MediaStreamKind.Video, Index = 0, PixelFormat = "yuv420p" }]
        };

        string[] backgrounds =
        [
            "song_background_1.png",
            "song_background_2.png",
            "song_background_3.png"
        ];

        var random = seed.HasValue ? new Random(seed.Value) : new Random();

        // use random ETV color by default
        string backgroundPath = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ResourcesCacheFolder,
            backgrounds[random.Next(backgrounds.Length)]);

        Option<string> watermarkPath = None;

        var boxBlur = false;

        const int HORIZONTAL_MARGIN_PERCENT = 3;
        var verticalMarginPercent = 5;
        const int WATERMARK_WIDTH_PERCENT = 25;
        WatermarkLocation watermarkLocation = random.Next(2) == 0
            ? WatermarkLocation.BottomLeft
            : WatermarkLocation.BottomRight;

        if (channel.SongVideoMode is ChannelSongVideoMode.WithProgress)
        {
            verticalMarginPercent += 10;
        }

        foreach (SongMetadata metadata in song.SongMetadata)
        {
            var fontSize = (int)Math.Round(channel.FFmpegProfile.Resolution.Height / 20.0);
            var largeFontSize = (int)Math.Round(channel.FFmpegProfile.Resolution.Height / 10.0);
            bool detailsStyle = random.Next(2) == 0;

            var sb = new StringBuilder();

            if (detailsStyle)
            {
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{{\\fs{largeFontSize}}}{metadata.Title}");
                }

                if (metadata.Artists.Count > 0)
                {
                    var allArtists = string.Join(", ", metadata.Artists);
                    sb.Append(CultureInfo.InvariantCulture, $"\\N{{\\fs{fontSize}}}{allArtists}");
                }
            }
            else
            {
                if (metadata.Artists.Count > 0)
                {
                    var allArtists = string.Join(", ", metadata.Artists);
                    sb.Append(allArtists);
                }

                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"\\N\"{metadata.Title}\"");
                }

                if (metadata.AlbumArtists.Count > 0)
                {
                    var allAlbumArtists = string.Join(
                        ", ",
                        metadata.AlbumArtists.Filter(aa => !metadata.Artists.Contains(aa)));
                    sb.Append(CultureInfo.InvariantCulture, $"\\N{allAlbumArtists}");
                }

                if (!string.IsNullOrWhiteSpace(metadata.Album))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"\\N{metadata.Album}");
                }
            }

            int leftMarginPercent = HORIZONTAL_MARGIN_PERCENT;
            int rightMarginPercent = HORIZONTAL_MARGIN_PERCENT;

            switch (watermarkLocation)
            {
                case WatermarkLocation.BottomLeft:
                    leftMarginPercent += WATERMARK_WIDTH_PERCENT + HORIZONTAL_MARGIN_PERCENT;
                    break;
                case WatermarkLocation.BottomRight:
                    leftMarginPercent = rightMarginPercent = HORIZONTAL_MARGIN_PERCENT;
                    rightMarginPercent += WATERMARK_WIDTH_PERCENT + HORIZONTAL_MARGIN_PERCENT;
                    break;
            }

            var leftMargin = (int)Math.Round(leftMarginPercent / 100.0 * channel.FFmpegProfile.Resolution.Width);
            var rightMargin = (int)Math.Round(rightMarginPercent / 100.0 * channel.FFmpegProfile.Resolution.Width);
            var verticalMargin =
                (int)Math.Round(verticalMarginPercent / 100.0 * channel.FFmpegProfile.Resolution.Height);

            subtitleFile = await new SubtitleBuilder(_tempFilePool)
                .WithResolution(channel.FFmpegProfile.Resolution)
                .WithFontName("OPTIKabel-Heavy")
                .WithFontSize(fontSize)
                .WithPrimaryColor("&HFFFFFF")
                .WithOutlineColor("&H444444")
                .WithAlignment(0)
                .WithMarginRight(rightMargin)
                .WithMarginLeft(leftMargin)
                .WithMarginV(verticalMargin)
                .WithBorderStyle(1)
                .WithShadow(3)
                .WithFormattedContent(sb.ToString())
                .BuildFile();

            // use thumbnail (cover art) if present
            // fall back to default art
            Artwork artwork = await Optional(metadata.Artwork.Find(a => a.ArtworkKind == ArtworkKind.Thumbnail))
                .IfNoneAsync(
                    new Artwork
                    {
                        Id = 0,
                        ArtworkKind = ArtworkKind.Thumbnail,
                        Path = _localFileSystem.GetCustomOrDefaultFile(FileSystemLayout.ResourcesCacheFolder, "song_album_cover_512.png")
                    });

            // signal that we want to use cover art as watermark
            videoVersion = new CoverArtMediaVersion
            {
                Chapters = [],
                // always stretch cover art
                Width = channel.FFmpegProfile.Resolution.Width / 10,
                Height = channel.FFmpegProfile.Resolution.Height / 10,
                SampleAspectRatio = "1:1",
                Streams = new List<MediaStream>
                {
                    new() { MediaStreamKind = MediaStreamKind.Video, Index = 0 }
                }
            };

            string customPath = _imageCache.GetPathForImage(
                artwork.Path,
                ArtworkKind.Thumbnail,
                Option<int>.None);

            watermarkPath = customPath;

            // only blurhash real album art
            if (artwork.Id > 0)
            {
                // randomize selected blur hash
                var hashes = new List<string>
                {
                    artwork.BlurHash43,
                    artwork.BlurHash54,
                    artwork.BlurHash64
                }.Filter(s => !string.IsNullOrWhiteSpace(s)).ToList();

                if (hashes.Count != 0)
                {
                    string hash = hashes[random.Next(hashes.Count)];

                    backgroundPath = await _imageCache.WriteBlurHash(hash, channel.FFmpegProfile.Resolution);

                    videoVersion.Height = channel.FFmpegProfile.Resolution.Height;
                    videoVersion.Width = channel.FFmpegProfile.Resolution.Width;
                }
                else
                {
                    backgroundPath = customPath;
                    boxBlur = true;
                }
            }
        }

        string videoPath = backgroundPath;

        videoVersion.MediaFiles = [new MediaFile { Path = videoPath }];

        Either<BaseError, string> maybeSongImage = await _ffmpegProcessService.GenerateSongImage(
            ffmpegPath,
            ffprobePath,
            subtitleFile,
            channel,
            videoVersion,
            videoPath,
            boxBlur,
            watermarkPath,
            watermarkLocation,
            HORIZONTAL_MARGIN_PERCENT,
            verticalMarginPercent,
            WATERMARK_WIDTH_PERCENT,
            cancellationToken);

        foreach (string si in maybeSongImage.RightToSeq())
        {
            videoPath = si;
            videoVersion = BackgroundImageMediaVersion.ForPath(
                si,
                channel.FFmpegProfile.Resolution,
                channel.SongVideoMode is ChannelSongVideoMode.WithProgress);
        }

        return Tuple(videoPath, videoVersion);
    }
}
