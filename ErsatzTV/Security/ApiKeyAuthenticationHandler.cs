using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ErsatzTV.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ErsatzTV.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiHelper.HeaderName, out var providedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        string expectedKey = ApiHelper.ApiKey;
        if (string.IsNullOrEmpty(expectedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key is not configured"));
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey.ToString());
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        if (providedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        Claim[] claims =
        [
            new(ClaimTypes.Name, "static-client"),
            new("client_id", "static-client")
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
