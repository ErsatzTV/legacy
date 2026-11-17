using System.Globalization;
using System.Text;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Iptv;

public class ChannelPlaylist
{
    private readonly string _baseUrl;
    private readonly List<Channel> _channels;
    private readonly string _host;
    private readonly string _scheme;
    private readonly string _userAgent;

    public ChannelPlaylist(
        string scheme,
        string host,
        string baseUrl,
        List<Channel> channels,
        string userAgent)
    {
        _scheme = scheme;
        _host = host;
        _baseUrl = baseUrl;
        _channels = channels;
        _userAgent = userAgent;
    }

    public string ToM3U()
    {
        var sb = new StringBuilder();

        var xmltv = $"{_scheme}://{_host}{_baseUrl}/iptv/xmltv.xml";
        sb.AppendLine(CultureInfo.InvariantCulture, $"#EXTM3U url-tvg=\"{xmltv}\" x-tvg-url=\"{xmltv}\"");
        foreach (Channel channel in _channels.OrderBy(c => decimal.Parse(c.Number, CultureInfo.InvariantCulture)))
        {
            if ((_userAgent ?? string.Empty).StartsWith("kodi", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("#KODIPROP:inputstream=inputstream.ffmpegdirect");

                string mimeType = channel.StreamingMode switch
                {
                    StreamingMode.TransportStream or StreamingMode.TransportStreamHybrid => "video/mp2t",
                    _ => "application/x-mpegURL"
                };
                sb.AppendLine(CultureInfo.InvariantCulture, $"#KODIPROP:mimetype={mimeType}");

                sb.AppendLine("#KODIPROP:inputstream.ffmpegdirect.open_mode=ffmpeg");
            }

            Option<Artwork> maybeArtwork = Optional(channel.Artwork).Flatten()
                .Filter(a => a.ArtworkKind == ArtworkKind.Logo)
                .HeadOrNone();
            var logo = $"{_scheme}://{_host}{_baseUrl}/iptv/logos/gen?text={channel.WebEncodedName}";
            foreach (Artwork artwork in maybeArtwork)
            {
                logo = artwork.IsExternalUrl()
                    ? artwork.Path
                    : $"{_scheme}://{_host}{_baseUrl}/iptv/logos/{artwork.Path}.jpg";
            }

            string shortUniqueId = Convert.ToBase64String(channel.UniqueId.ToByteArray())
                .TrimEnd('=')
                .Replace("/", "_")
                .Replace("+", "-");

            string format = channel.StreamingMode switch
            {
                StreamingMode.HttpLiveStreamingDirect => "m3u8?mode=hls-direct",
                StreamingMode.HttpLiveStreamingSegmenter => "m3u8?mode=segmenter",
                StreamingMode.TransportStreamHybrid => "ts",
                _ => "ts?mode=ts-legacy"
            };

            string vcodec = channel.FFmpegProfile.VideoFormat.ToString().ToLowerInvariant();
            string acodec = channel.FFmpegProfile.AudioFormat.ToString().ToLowerInvariant();

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"#EXTINF:0 tvg-id=\"{ChannelIdentifier.FromNumber(channel.Number)}\" channel-id=\"{shortUniqueId}\" channel-number=\"{channel.Number}\" CUID=\"{shortUniqueId}\" tvg-chno=\"{channel.Number}\" tvg-name=\"{channel.Name}\" tvg-logo=\"{logo}\" group-title=\"{channel.Group}\" tvc-stream-vcodec=\"{vcodec}\" tvc-stream-acodec=\"{acodec}\", {channel.Name}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{_scheme}://{_host}{_baseUrl}/iptv/channel/{channel.Number}.{format}");
        }

        return sb.ToString();
    }
}
