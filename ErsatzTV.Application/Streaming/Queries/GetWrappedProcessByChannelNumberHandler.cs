using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Streaming;

public class GetWrappedProcessByChannelNumberHandler : FFmpegProcessHandler<GetWrappedProcessByChannelNumber>
{
    private readonly IFFmpegProcessService _ffmpegProcessService;

    public GetWrappedProcessByChannelNumberHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IFFmpegProcessService ffmpegProcessService)
        : base(dbContextFactory) =>
        _ffmpegProcessService = ffmpegProcessService;

    protected override async Task<Either<BaseError, PlayoutItemProcessModel>> GetProcess(
        TvContext dbContext,
        GetWrappedProcessByChannelNumber request,
        Channel channel,
        string ffmpegPath,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        bool saveReports = await dbContext.ConfigElements
            .GetValue<bool>(ConfigElementKey.FFmpegSaveReports, cancellationToken)
            .Map(result => result.IfNone(false));

        Command process = await _ffmpegProcessService.WrapSegmenter(
            ffmpegPath,
            saveReports,
            channel,
            request.Scheme,
            request.Host,
            cancellationToken);

        return new PlayoutItemProcessModel(
            process,
            Option<GraphicsEngineContext>.None,
            Option<TimeSpan>.None,
            DateTimeOffset.MaxValue,
            true,
            Option<long>.None,
            Option<int>.None,
            Option<TimeSpan>.None,
            false);
    }
}
