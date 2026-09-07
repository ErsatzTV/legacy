namespace ErsatzTV.Core.FFmpeg;

public record HlsSessionModel(
    string ChannelNumber,
    string State,
    DateTimeOffset? TranscodedUntil,
    DateTimeOffset LastAccess);
