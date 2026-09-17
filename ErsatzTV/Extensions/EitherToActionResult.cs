using System.Diagnostics.CodeAnalysis;
using ErsatzTV.Core;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Extensions;

[SuppressMessage("ReSharper", "VSTHRD003")]
public static class EitherToActionResult
{
    public static Task<IActionResult> ToActionResult<TRight>(this Task<Either<BaseError, TRight>> either) =>
        either.Map(Match);

    private static IActionResult Match<TRight>(this Either<BaseError, TRight> either) =>
        either.Match<IActionResult>(
            Left: l =>
            {
                return l switch
                {
                    { Kind: BaseErrorKind.BadRequest } => new BadRequestObjectResult(l.ToString()),
                    { Kind: BaseErrorKind.NotFound } => new NotFoundObjectResult(l.ToString()),
                    { Kind: BaseErrorKind.Conflict } => new ConflictObjectResult(l.ToString()),
                    _ => new ObjectResult(l.ToString()) { StatusCode = StatusCodes.Status500InternalServerError }
                };
            },
            Right: r => new OkObjectResult(r));
}
