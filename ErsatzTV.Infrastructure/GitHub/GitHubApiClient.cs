using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.GitHub;
using Refit;

namespace ErsatzTV.Infrastructure.GitHub;

public class GitHubApiClient : IGitHubApiClient
{
    public async Task<Either<BaseError, string>> GetLatestReleaseNotes(CancellationToken cancellationToken)
    {
        try
        {
            IGitHubApi service = RestService.For<IGitHubApi>("https://api.github.com");
            return await service.GetReleases(cancellationToken).Map(releases => releases.Head().Body);
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public async Task<Either<BaseError, string>> GetReleaseNotes(string tag, CancellationToken cancellationToken)
    {
        try
        {
            IGitHubApi service = RestService.For<IGitHubApi>("https://api.github.com");
            return await service.GetTag(tag, cancellationToken).Map(t => t.Body);
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }
}
