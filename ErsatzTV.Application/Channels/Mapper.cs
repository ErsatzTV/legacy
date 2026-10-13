using ErsatzTV.Application.Artworks;
using ErsatzTV.Core.Api.Channels;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Channels;

internal static class Mapper
{
    internal static ChannelViewModel ProjectToViewModel(Channel channel, int playoutCount) =>
        new(
            channel.Id,
            channel.Number,
            channel.Name,
            channel.Group,
            channel.Categories,
            channel.FFmpegProfileId,
            channel.SlugSeconds,
            GetLogo(channel),
            channel.StreamSelectorMode,
            channel.StreamSelector,
            channel.PreferredAudioLanguageCode,
            channel.PreferredAudioTitle,
            channel.PlayoutSource,
            channel.PlayoutMode,
            channel.MirrorSourceChannelId,
            channel.PlayoutOffset,
            channel.StreamingEngine,
            channel.NextEngineTextSubtitleMode,
            channel.StreamingMode,
            channel.WatermarkId,
            channel.FallbackFillerId,
            playoutCount,
            channel.PreferredSubtitleLanguageCode,
            channel.SubtitleMode,
            channel.MusicVideoCreditsMode,
            channel.MusicVideoCreditsTemplate,
            channel.SongVideoMode,
            channel.TranscodeMode,
            channel.IdleBehavior,
            channel.IsEnabled,
            channel.ShowInEpg);

    internal static ChannelResponseModel ProjectToResponseModel(Channel channel) =>
        new(
            channel.Id,
            channel.Number,
            channel.Name,
            channel.FFmpegProfile.Name,
            channel.PreferredAudioLanguageCode,
            GetStreamingMode(channel));

    internal static ResolutionViewModel ProjectToViewModel(Resolution resolution) =>
        new(resolution.Height, resolution.Width);

    internal static ChannelStreamingSpecsViewModel ProjectToSpecsViewModel(Channel channel) =>
        new(
            channel.FFmpegProfile.Resolution.Height,
            channel.FFmpegProfile.Resolution.Width,
            (int)((channel.FFmpegProfile.VideoBitrate * 1000 + channel.FFmpegProfile.AudioBitrate * 1000) * 1.2),
            channel.FFmpegProfile.VideoFormat,
            channel.FFmpegProfile.VideoProfile,
            channel.FFmpegProfile.AudioFormat,
            channel.NextEngineTextSubtitleMode);

    private static ArtworkContentTypeModel GetLogo(Channel channel)
    {
        Option<Artwork> maybeArtwork = channel.Artwork
            .Where(a => a.ArtworkKind == ArtworkKind.Logo)
            .HeadOrNone();

        foreach (Artwork artwork in maybeArtwork)
        {
            return artwork.IsExternalUrl()
                ? new ArtworkContentTypeModel(artwork.Path, string.Empty)
                : new ArtworkContentTypeModel($"iptv/logos/{artwork.Path}", artwork.OriginalContentType);
        }

        return ArtworkContentTypeModel.None;
    }

    private static string GetStreamingMode(Channel channel) =>
        channel.StreamingMode switch
        {
            StreamingMode.TransportStream => "MPEG-TS (Legacy)",
            StreamingMode.TransportStreamHybrid => "MPEG-TS",
            StreamingMode.HttpLiveStreamingDirect => "HLS Direct",
            StreamingMode.HttpLiveStreamingSegmenter => "HLS Segmenter",
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };
}
