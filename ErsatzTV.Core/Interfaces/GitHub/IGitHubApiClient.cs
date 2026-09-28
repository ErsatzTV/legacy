namespace ErsatzTV.Core.Interfaces.GitHub;

public interface IGitHubApiClient
{
    Task<Either<BaseError, string>> GetLatestReleaseNotes(CancellationToken cancellationToken);
    Task<Either<BaseError, string>> GetReleaseNotes(string tag, CancellationToken cancellationToken);
    Task<Either<BaseError, string>> GetDevelopReleaseNotes(string tag, CancellationToken cancellationToken);
}
