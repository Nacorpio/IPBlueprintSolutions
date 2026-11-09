using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Admin;

/// <summary>
/// Ties each admin login to the password it was made with, so changing <c>Admin:Password</c> logs out every session.
/// The cookie holds a short fingerprint of the password, never the password itself (and the cookie is encrypted).
/// </summary>
public static class AdminSession
{
    private const string PasswordClaim = "admin-password-version";

    public static ClaimsPrincipal CreatePrincipal(AdminOptions options) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(PasswordClaim, Fingerprint(options.Password))],
            AdminPolicy.Scheme));

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<AdminOptions>>().CurrentValue;
        var claim = context.Principal?.FindFirstValue(PasswordClaim);
        if (options.IsConfigured && claim is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(claim), Encoding.ASCII.GetBytes(Fingerprint(options.Password))))
            return;

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AdminPolicy.Scheme);
    }

    private static string Fingerprint(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ipb-admin-session:" + password)), 0, 16);
}
