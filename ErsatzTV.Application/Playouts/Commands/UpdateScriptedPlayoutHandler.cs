using System.CommandLine.Parsing;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Playouts;

public class
    UpdateScriptedPlayoutHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ChannelWriter<IBackgroundServiceRequest> workerChannel,
        IFileSystem fileSystem)
    : IRequestHandler<UpdateScriptedPlayout,
        Either<BaseError, PlayoutNameViewModel>>
{
    public async Task<Either<BaseError, PlayoutNameViewModel>> Handle(
        UpdateScriptedPlayout request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Playout> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(playout => ApplyUpdateRequest(dbContext, request, playout, cancellationToken));
    }

    private async Task<PlayoutNameViewModel> ApplyUpdateRequest(
        TvContext dbContext,
        UpdateScriptedPlayout request,
        Playout playout,
        CancellationToken cancellationToken)
    {
        playout.ScheduleFile = request.ScheduleFile;

        if (await dbContext.SaveChangesAsync(cancellationToken) > 0)
        {
            await workerChannel.WriteAsync(new RefreshChannelData(playout.Channel.Number), cancellationToken);
        }

        return new PlayoutNameViewModel(
            playout.Id,
            playout.ScheduleKind,
            playout.Channel.Name,
            playout.Channel.Number,
            playout.Channel.PlayoutMode,
            playout.ProgramSchedule?.Name ?? string.Empty,
            playout.ScheduleFile,
            playout.DailyRebuildTime,
            playout.BuildStatus);
    }

    private async Task<Validation<BaseError, Playout>> Validate(
        TvContext dbContext,
        UpdateScriptedPlayout request,
        CancellationToken cancellationToken) =>
        (ValidateScheduleFile(request), await PlayoutMustExist(dbContext, request, cancellationToken))
        .Apply((_, playout) => playout);

    private Validation<BaseError, string> ValidateScheduleFile(UpdateScriptedPlayout request)
    {
        var args = CommandLineParser.SplitCommandLine(request.ScheduleFile).ToList();
        if (args.Count == 0)
        {
            return BaseError.New("Scripted schedule does not exist!");
        }

        string scriptFile = args[0];
        if (!fileSystem.File.Exists(scriptFile))
        {
            return BaseError.New("Scripted schedule does not exist!");
        }

        return request.ScheduleFile;
    }

    private static Task<Validation<BaseError, Playout>> PlayoutMustExist(
        TvContext dbContext,
        UpdateScriptedPlayout updatePlayout,
        CancellationToken cancellationToken) =>
        dbContext.Playouts
            .Include(p => p.Channel)
            .SelectOneAsync(p => p.Id, p => p.Id == updatePlayout.PlayoutId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>("Playout does not exist."));
}
