using System.Globalization;
using System.Text;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;
using Scriban;

namespace ErsatzTV.Infrastructure.FFmpeg;

public class MusicVideoCreditsGenerator(ITempFilePool tempFilePool, ILogger<MusicVideoCreditsGenerator> logger)
    : IMusicVideoCreditsGenerator
{
    public async Task<Option<Subtitle>> GenerateCreditsSubtitle(MusicVideo musicVideo, FFmpegProfile ffmpegProfile)
    {
        const int horizontalMarginPercent = 3;
        const int verticalMarginPercent = 5;

        var fontSize = (int)Math.Round(ffmpegProfile.Resolution.Height / 20.0);

        int leftMarginPercent = horizontalMarginPercent;
        int rightMarginPercent = horizontalMarginPercent;

        var leftMargin = (int)Math.Round(leftMarginPercent / 100.0 * ffmpegProfile.Resolution.Width);
        var rightMargin = (int)Math.Round(rightMarginPercent / 100.0 * ffmpegProfile.Resolution.Width);
        var verticalMargin =
            (int)Math.Round(verticalMarginPercent / 100.0 * ffmpegProfile.Resolution.Height);

        foreach (MusicVideoMetadata metadata in musicVideo.MusicVideoMetadata)
        {
            var sb = new StringBuilder();

            string artist = string.Empty;
            foreach (ArtistMetadata artistMetadata in Optional(metadata.MusicVideo?.Artist?.ArtistMetadata).Flatten())
            {
                artist = artistMetadata.Title;
            }

            if (!string.IsNullOrWhiteSpace(artist))
            {
                sb.Append(artist);
            }

            if (!string.IsNullOrWhiteSpace(metadata.Title))
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\N\"{metadata.Title}\"");
            }

            if (!string.IsNullOrWhiteSpace(metadata.Album))
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\N{metadata.Album}");
            }

            string subtitles = await new SubtitleBuilder(tempFilePool)
                .WithResolution(ffmpegProfile.Resolution)
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
                .WithStartEnd(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(16))
                .WithFade(true)
                .BuildFile();

            return new Subtitle
            {
                Codec = "ass",
                Default = true,
                Forced = true,
                IsExtracted = false,
                SubtitleKind = SubtitleKind.Generated,
                Path = subtitles,
                SDH = false
            };
        }

        return None;
    }

    public async Task<Option<Subtitle>> GenerateCreditsSubtitleFromTemplate(
        MusicVideo musicVideo,
        FFmpegProfile ffmpegProfile,
        Option<TimeSpan> streamSeek,
        string templateFileName)
    {
        try
        {
            string text = await File.ReadAllTextAsync(templateFileName);
            var template = Template.Parse(text, templateFileName);
            foreach (MusicVideoMetadata metadata in musicVideo.MusicVideoMetadata)
            {
                string artist = string.Empty;
                foreach (ArtistMetadata artistMetadata in Optional(musicVideo.Artist?.ArtistMetadata).Flatten())
                {
                    artist = artistMetadata.Title;
                }

                string result = await template.RenderAsync(
                    new
                    {
                        ffmpegProfile.Resolution,
                        metadata.Title,
                        metadata.Track,
                        metadata.Album,
                        metadata.Plot,
                        metadata.ReleaseDate,
                        AllArtists = (metadata.Artists ?? []).Map(a => a.Name),
                        Artist = artist,
                        Studios = (metadata.Studios ?? []).Map(s => s.Name),
                        Directors = (metadata.Directors ?? []).Map(s => s.Name),
                        musicVideo.GetHeadVersion().Duration,
                        StreamSeek = await streamSeek.IfNoneAsync(TimeSpan.Zero)
                    });

                string fileName = tempFilePool.GetNextTempFile(TempFileCategory.Subtitle);
                await File.WriteAllTextAsync(fileName, result);
                return new Subtitle
                {
                    Codec = "ass",
                    Default = true,
                    Forced = true,
                    IsExtracted = false,
                    SubtitleKind = SubtitleKind.Generated,
                    Path = fileName,
                    SDH = false
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating music video credits from template {Template}", templateFileName);
        }

        return None;
    }
}
