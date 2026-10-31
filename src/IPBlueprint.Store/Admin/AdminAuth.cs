using System.Security.Cryptography;
using System.Text;

namespace IPBlueprint.Store.Admin;

/// <summary>Bound from the "Admin" section. One shared password for the shop owners.
/// Set it with user-secrets locally and an environment variable (Admin__Password) on the server.</summary>
public class AdminOptions
{
    public const string Section = "Admin";

    public string Password { get; set; } = "";

    public bool IsConfigured => Password.Length >= 12;

    public bool Check(string? attempt)
    {
        if (!IsConfigured || string.IsNullOrEmpty(attempt)) return false;
        // Hash both so the comparison is constant-time regardless of length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(attempt)),
            SHA256.HashData(Encoding.UTF8.GetBytes(Password)));
    }
}

public static class AdminPolicy
{
    public const string Name = "Admin";
    public const string Scheme = "AdminCookie";
    public const string LoginRateLimit = "admin-login";
}
