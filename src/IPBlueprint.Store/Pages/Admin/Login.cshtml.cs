using System.Security.Claims;
using IPBlueprint.Store.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages.Admin;

[EnableRateLimiting(AdminPolicy.LoginRateLimit)]
public class LoginModel(IOptions<AdminOptions> options, ILogger<LoginModel> logger) : PageModel
{
    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }
    public bool IsConfigured => options.Value.IsConfigured;

    public void OnGet(string? returnUrl) => ReturnUrl = returnUrl;

    public async Task<IActionResult> OnPostAsync(string? password, string? returnUrl)
    {
        ReturnUrl = returnUrl;
        if (!options.Value.Check(password))
        {
            logger.LogWarning("Failed admin login from {Ip}", HttpContext.Connection.RemoteIpAddress);
            Error = "Wrong password.";
            return Page();
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], AdminPolicy.Scheme);
        await HttpContext.SignInAsync(AdminPolicy.Scheme, new ClaimsPrincipal(identity));

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/Admin");
    }
}
