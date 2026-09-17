using System.Linq.Expressions;
using ErsatzTV.Core;

namespace ErsatzTV.Application;

public static partial class Validators
{
    public static Func<Expression<Func<T, string>>, Validation<BaseError, string>> NotLongerThan<T>(
        this T input,
        int maxLength) =>
        expression => Optional(expression)
            .Map(exp => exp.Compile()(input))
            .Where(s => s.Length <= maxLength)
            .ToValidation(BaseError.BadRequest($"[{GetMemberName(expression)}] must not be longer than {maxLength}"));

    public static Validation<BaseError, string> NotEmpty<T>(this T input, Expression<Func<T, string>> expression) =>
        Optional(expression.Compile()(input))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToValidation(BaseError.BadRequest($"[{GetMemberName(expression)}] is an empty string"));
}
