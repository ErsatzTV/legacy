using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.FFmpegProfiles;

public class DeleteFFmpegProfileHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IConfigElementRepository configElementRepository,
    ISearchTargets searchTargets)
    : IRequestHandler<DeleteFFmpegProfile, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteFFmpegProfile request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, FFmpegProfile> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(p => DoDeletion(dbContext, p));
    }

    private async Task<Unit> DoDeletion(TvContext dbContext, FFmpegProfile ffmpegProfile)
    {
        dbContext.FFmpegProfiles.Remove(ffmpegProfile);
        await dbContext.SaveChangesAsync();
        searchTargets.SearchTargetsChanged();
        return Unit.Default;
    }

    private async Task<Validation<BaseError, FFmpegProfile>> Validate(
        TvContext dbContext,
        DeleteFFmpegProfile request,
        CancellationToken cancellationToken) =>
        (await FFmpegProfileMustNotBeUsed(dbContext, request, cancellationToken),
            await FFmpegProfileMustNotBeDefault(request, cancellationToken),
            await FFmpegProfileMustExist(dbContext, request, cancellationToken))
        .Apply((_, _, ffmpegProfile) => ffmpegProfile);

    private static Task<Validation<BaseError, FFmpegProfile>> FFmpegProfileMustExist(
        TvContext dbContext,
        DeleteFFmpegProfile request,
        CancellationToken cancellationToken) =>
        dbContext.FFmpegProfiles
            .SelectOneAsync(p => p.Id, p => p.Id == request.FFmpegProfileId, cancellationToken)
            .Map(o => o.ToValidation(BaseError.NotFound($"FFmpegProfile {request.FFmpegProfileId} does not exist")));

    private static async Task<Validation<BaseError, Unit>> FFmpegProfileMustNotBeUsed(
        TvContext dbContext,
        DeleteFFmpegProfile request,
        CancellationToken cancellationToken)
    {
        int count = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.FFmpegProfileId == request.FFmpegProfileId)
            .CountAsync(cancellationToken);

        if (count > 0)
        {
            return BaseError.Conflict(
                $"Cannot delete FFmpeg Profile that is used by {count} {(count > 1 ? "channels" : "channel")}");
        }

        return Unit.Default;
    }

    private async Task<Validation<BaseError, Unit>> FFmpegProfileMustNotBeDefault(
        DeleteFFmpegProfile request,
        CancellationToken cancellationToken)
    {
        Option<int> defaultFFmpegProfileId =
            await configElementRepository.GetValue<int>(ConfigElementKey.FFmpegDefaultProfileId, cancellationToken);

        if (defaultFFmpegProfileId.Any(id => id == request.FFmpegProfileId))
        {
            return BaseError.BadRequest("Cannot delete default FFmpeg Profile");
        }

        return Unit.Default;
    }
}
