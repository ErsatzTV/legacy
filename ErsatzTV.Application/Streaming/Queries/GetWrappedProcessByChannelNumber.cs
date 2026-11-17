using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Streaming;

public record GetWrappedProcessByChannelNumber : FFmpegProcessRequest
{
    public GetWrappedProcessByChannelNumber(
        string scheme,
        string host,
        string channelNumber) : base(
        channelNumber,
        StreamingMode.TransportStreamHybrid,
        DateTimeOffset.Now,
        false,
        true,
        DateTimeOffset.Now, // unused
        TimeSpan.Zero,
        Option<int>.None)
    {
        Scheme = scheme;
        Host = host;
    }

    public string Scheme { get; }
    public string Host { get; }
}
