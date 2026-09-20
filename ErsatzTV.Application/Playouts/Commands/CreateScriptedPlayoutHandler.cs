using System.CommandLine.Parsing;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Channel = ErsatzTV.Core.Domain.Channel;

namespace ErsatzTV.Application.Playouts;

public class CreateScriptedPlayoutHandler(
    IFileSystem fileSystem,
    ChannelWriter<IBackgroundServiceRequest> channel,
    IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<CreateScriptedPlayout, Either<BaseError, CreatePlayoutResponse>>
{
    public async Task<Either<BaseError, CreatePlayoutResponse>> Handle(
        CreateScriptedPlayout request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Playout> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(playout => PersistPlayout(dbContext, playout, cancellationToken));
    }

    private async Task<CreatePlayoutResponse> PersistPlayout(
        TvContext dbContext,
        Playout playout,
        CancellationToken cancellationToken)
    {
        await dbContext.Playouts.AddAsync(playout, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await channel.WriteAsync(new BuildPlayout(playout.Id, PlayoutBuildMode.Reset), cancellationToken);
        if (playout.Channel.PlayoutMode is ChannelPlayoutMode.OnDemand)
        {
            await channel.WriteAsync(
                new TimeShiftOnDemandPlayout(playout.Id, DateTimeOffset.Now, false),
                cancellationToken);
        }

        await channel.WriteAsync(new RefreshChannelList(), cancellationToken);
        return new CreatePlayoutResponse(playout.Id);
    }

    private async Task<Validation<BaseError, Playout>> Validate(
        TvContext dbContext,
        CreateScriptedPlayout request,
        CancellationToken cancellationToken) =>
        (await ValidateChannel(dbContext, request, cancellationToken), ValidateScheduleFile(request),
            ValidateScheduleKind(request))
        .Apply((channel, yamlFile, scheduleKind) => new Playout
        {
            ChannelId = channel.Id,
            ScheduleFile = yamlFile,
            ScheduleKind = scheduleKind,
            Seed = new Random().Next()
        });

    private static Task<Validation<BaseError, Channel>> ValidateChannel(
        TvContext dbContext,
        CreateScriptedPlayout createScriptedPlayout,
        CancellationToken cancellationToken) =>
        dbContext.Channels
            .Include(c => c.Playouts)
            .SelectOneAsync(c => c.Id, c => c.Id == createScriptedPlayout.ChannelId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>("Channel does not exist"))
            .BindT(ChannelMustNotHavePlayouts);

    private static Validation<BaseError, Channel> ChannelMustNotHavePlayouts(Channel channel) =>
        Optional(channel.Playouts.Count)
            .Filter(count => count == 0)
            .Map(_ => channel)
            .ToValidation<BaseError>("Channel already has one playout");

    private Validation<BaseError, string> ValidateScheduleFile(CreateScriptedPlayout request)
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

    private static Validation<BaseError, PlayoutScheduleKind> ValidateScheduleKind(
        CreateScriptedPlayout createScriptedPlayout) =>
        Optional(createScriptedPlayout.ScheduleKind)
            .Filter(scheduleKind => scheduleKind == PlayoutScheduleKind.Scripted)
            .ToValidation<BaseError>("[ScheduleKind] must be Scripted");
}
