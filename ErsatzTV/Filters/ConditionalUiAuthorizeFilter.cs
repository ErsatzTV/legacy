using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ErsatzTV.Filters;

public class ConditionalUiAuthorizeFilter : AuthorizeFilter
{
    public ConditionalUiAuthorizeFilter() : base(
        new AuthorizationPolicyBuilder().AddAuthenticationSchemes("cookie").RequireAuthenticatedUser().Build())
    {
    }

    public override Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // the browser requests these directly (video player, window.open) so an api key header is not possible;
        // authorize with the management ui cookie instead, which only exists when oidc is configured
        if (OidcHelper.IsEnabled)
        {
            return base.OnAuthorizationAsync(context);
        }

        return Task.CompletedTask;
    }
}
