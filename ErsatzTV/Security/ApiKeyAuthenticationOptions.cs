using Microsoft.AspNetCore.Authentication;

namespace ErsatzTV.Security;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
}
