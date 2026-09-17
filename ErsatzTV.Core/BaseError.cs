namespace ErsatzTV.Core;

public class BaseError : NewType<BaseError, string>
{
    public BaseError(string value) : base(value)
    {
    }

    public static BaseError BadRequest(string value) => new(value) { Kind = BaseErrorKind.BadRequest };

    public static BaseError NotFound(string value) => new(value) { Kind = BaseErrorKind.NotFound };

    public static BaseError Conflict(string value) => new(value) { Kind = BaseErrorKind.Conflict };

    public BaseErrorKind Kind { get; set; }

    public static implicit operator BaseError(string str) => New(str);
}

public enum BaseErrorKind
{
    Unknown,
    BadRequest,
    NotFound,
    Conflict
}

public static class ErrorExtensions
{
    public static BaseError Join(this Seq<BaseError> errors)
    {
        if (errors.Count == 1)
        {
            return errors.Head();
        }

        var kinds = errors.Map(e => e.Kind).Distinct().ToList();
        var kind = kinds.Count == 1 ? kinds[0]
            : kinds.Contains(BaseErrorKind.Unknown) ? BaseErrorKind.Unknown
            : BaseErrorKind.BadRequest;

        return new BaseError(string.Join("; ", errors)) { Kind = kind };
    }
}
