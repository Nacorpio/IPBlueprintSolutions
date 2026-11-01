namespace IPBlueprint.Store.Shop;

/// <summary>
/// Optional visitor statistics from a cookie-free service, bound from the "Analytics" section. Off unless configured.
/// Both supported services count visits without cookies or personal data, so the shop needs no consent banner.
/// <list type="bullet">
/// <item><b>Plausible</b>: set Provider to "Plausible" and ScriptUrl to the script address from your site's
/// installation page (it looks like https://plausible.io/js/pa-XXXXXXXX.js).</item>
/// <item><b>Umami</b>: set Provider to "Umami" and WebsiteId to the id from the Umami dashboard. ScriptUrl defaults to
/// Umami Cloud; set it to your own server's script.js if you host Umami yourself.</item>
/// </list>
/// </summary>
public class AnalyticsOptions
{
    public const string Section = "Analytics";

    public const string Plausible = "Plausible";
    public const string Umami = "Umami";

    public const string UmamiCloudScript = "https://cloud.umami.is/script.js";

    /// <summary>"Plausible", "Umami", or empty for no statistics.</summary>
    public string Provider { get; set; } = "";

    public string ScriptUrl { get; set; } = "";

    /// <summary>Umami only.</summary>
    public string WebsiteId { get; set; } = "";

    public bool IsPlausible => string.Equals(Provider?.Trim(), Plausible, StringComparison.OrdinalIgnoreCase);
    public bool IsUmami => string.Equals(Provider?.Trim(), Umami, StringComparison.OrdinalIgnoreCase);

    /// <summary>The script to load, or null when statistics are off or the settings are incomplete.</summary>
    public string? Script
    {
        get
        {
            var url = ScriptUrl?.Trim();
            if (IsUmami && string.IsNullOrEmpty(url)) url = UmamiCloudScript;
            if (!IsPlausible && !IsUmami) return null;
            if (IsUmami && string.IsNullOrWhiteSpace(WebsiteId)) return null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
        }
    }

    public bool Enabled => Script is not null;

    /// <summary>The service's name for the privacy policy.</summary>
    public string DisplayName => IsUmami ? Umami : Plausible;
}
