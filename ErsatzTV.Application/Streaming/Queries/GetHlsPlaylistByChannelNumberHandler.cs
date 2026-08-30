using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ErsatzTV.Application.Streaming;

public class GetHlsPlaylistByChannelNumberHandler :
    IRequestHandler<GetHlsPlaylistByChannelNumber, Either<BaseError, string>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IMemoryCache _memoryCache;

    public GetHlsPlaylistByChannelNumberHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IConfigElementRepository configElementRepository,
        IMemoryCache memoryCache)
    {
        _dbContextFactory = dbContextFactory;
        _configElementRepository = configElementRepository;
        _memoryCache = memoryCache;
    }

    public async Task<Either<BaseError, string>> Handle(
        GetHlsPlaylistByChannelNumber request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.Now;
        Validation<BaseError, Parameters> validation = await Validate(dbContext, request, now, cancellationToken);
        return await validation.Apply(parameters => GetPlaylist(request, parameters, now));
    }

    private async Task<string> GetPlaylist(
        GetHlsPlaylistByChannelNumber request,
        Parameters parameters,
        DateTimeOffset now)
    {
        string mode = request.Mode switch
        {
            "segmenter" or "ts-legacy" or "ts" => $"&mode={request.Mode}",
            // "hls-direct" => string.Empty,
            _ => string.Empty
        };

        string endpoint = "internal/ffmpeg/stream";
        string extension = string.Empty;

        if (request.Mode is "hls-direct")
        {
            endpoint = "iptv/hls-direct";

            OutputFormatKind outputFormat = await _configElementRepository
                .GetValue<OutputFormatKind>(ConfigElementKey.FFmpegHlsDirectOutputFormat, CancellationToken.None)
                .IfNoneAsync(OutputFormatKind.MpegTs);

            extension = outputFormat switch
            {
                OutputFormatKind.MpegTs => ".ts",
                OutputFormatKind.Mp4 => ".mp4",
                OutputFormatKind.Mkv => ".mkv",
                _ => string.Empty
            };
        }

        string accessToken = string.IsNullOrWhiteSpace(request.AccessToken)
            ? string.Empty
            : $"&access_token={request.AccessToken}";

        long index = GetIndexForChannel(parameters.Channel, parameters.PlayoutItem);
        double timeRemaining = Math.Abs((parameters.PlayoutItem.FinishOffset - now).TotalSeconds);
        return $@"#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:{index}
#EXT-X-DISCONTINUITY
#EXTINF:{timeRemaining:F2},
{request.Scheme}://{request.Host}/{endpoint}/{request.ChannelNumber}{extension}?index={index}{mode}{accessToken}
";
    }

    private static Task<Validation<BaseError, Parameters>> Validate(
        TvContext dbContext,
        GetHlsPlaylistByChannelNumber request,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ChannelMustExist(dbContext, request, cancellationToken)
            .BindT(channel => PlayoutItemMustExist(dbContext, channel, now));

    private static Task<Validation<BaseError, Channel>> ChannelMustExist(
        TvContext dbContext,
        GetHlsPlaylistByChannelNumber request,
        CancellationToken cancellationToken) =>
        dbContext.Channels
            .SelectOneAsync(c => c.Number, c => c.Number == request.ChannelNumber, cancellationToken)
            .Map(o => o.ToValidation<BaseError>($"Channel number {request.ChannelNumber} does not exist."));

    private static Task<Validation<BaseError, Parameters>> PlayoutItemMustExist(
        TvContext dbContext,
        Channel channel,
        DateTimeOffset now) =>
        dbContext.PlayoutItems
            .ForChannelAndTime(channel.Id, now)
            .MapT(playoutItem => new Parameters(channel, playoutItem))
            .Map(o => o.ToValidation<BaseError>($"Unable to locate playout item for channel {channel.Number}"));

    private long GetIndexForChannel(Channel channel, PlayoutItem playoutItem)
    {
        long ticks = playoutItem.Start.Ticks;
        var key = new ChannelIndexKey(channel.Id);

        long index;
        if (_memoryCache.TryGetValue(key, out ChannelIndexRecord channelRecord))
        {
            if (channelRecord.StartTicks == ticks)
            {
                index = channelRecord.Index;
            }
            else
            {
                index = channelRecord.Index + 1;
                _memoryCache.Set(key, new ChannelIndexRecord(ticks, index), TimeSpan.FromDays(1));
            }
        }
        else
        {
            index = 1;
            _memoryCache.Set(key, new ChannelIndexRecord(ticks, index), TimeSpan.FromDays(1));
        }

        return index;
    }

    private sealed record ChannelIndexKey(int ChannelId);

    private sealed record ChannelIndexRecord(long StartTicks, long Index);

    private sealed record Parameters(Channel Channel, PlayoutItem PlayoutItem);
}
