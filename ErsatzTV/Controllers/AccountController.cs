using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

[ApiController]
public class AccountController : ControllerBase
{
    private static readonly string[] AuthenticationSchemes = ["oidc", "cookie"];

    [HttpPost("account/logout")]
    public IActionResult Logout() => new SignOutResult(AuthenticationSchemes);
}
