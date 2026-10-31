using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace IPBlueprint.Store.Localization;

/// <summary>
/// The site is Swedish by default; visitors can switch to English, and the choice is remembered in a cookie.
/// Only the UI language changes. Number and date formatting stays culture-neutral so form posts (e.g. the
/// admin price field, which browsers always send as "249.50") parse the same way whichever language is shown.
/// </summary>
public static class Languages
{
    public const string Swedish = "sv";
    public const string English = "en";

    public static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
    public static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    public static readonly IReadOnlyList<CultureInfo> UiCultures = [SwedishCulture, EnglishCulture];

    public static string CookieName => CookieRequestCultureProvider.DefaultCookieName;

    /// <summary>"?lang=en" on any page shows it in English. This gives each language its own address, which search
    /// engines need (they don't keep cookies), and the language switch uses it too.</summary>
    public const string QueryKey = "lang";

    /// <summary>"sv" or "en" for the language the current request is shown in.</summary>
    public static string Current => IsEnglish ? English : Swedish;

    public static bool IsEnglish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == English;

    /// <summary>Maps "sv"/"en" (or anything else, which counts as Swedish) to its culture.</summary>
    public static CultureInfo CultureFor(string? language) => Normalize(language) == English ? EnglishCulture : SwedishCulture;

    public static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Swedish;

    public static void Configure(RequestLocalizationOptions o)
    {
        o.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, SwedishCulture);
        o.SupportedCultures = [CultureInfo.InvariantCulture];
        o.SupportedUICultures = [.. UiCultures];
        o.FallBackToParentUICultures = true;
        // Only the visitor's own choice counts. The browser's Accept-Language is ignored on purpose: the shop is Swedish
        // unless someone presses "English".
        // "?lang=" in the address wins over the cookie, so a link to the English page always shows English.
        o.RequestCultureProviders = [new QueryStringLanguageProvider(), new CookieRequestCultureProvider()];
    }

    /// <summary>The language asked for with "?lang=sv" or "?lang=en", or null when the address doesn't say.</summary>
    public static string? FromQuery(HttpRequest request) =>
        request.Query[QueryKey].ToString().ToLowerInvariant() switch
        {
            Swedish => Swedish,
            English => English,
            _ => null,
        };

    /// <summary>Remembers the visitor's language for a year. The visitor asked for it, so it needs no consent banner.</summary>
    public static void Remember(HttpContext http, string language) =>
        http.Response.Cookies.Append(CookieName, CookieValue(language), new CookieOptions
        {
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
        });

    /// <summary>True when the visitor's language cookie already says <paramref name="language"/>.</summary>
    public static bool IsRemembered(HttpRequest request, string language) =>
        request.Cookies.TryGetValue(CookieName, out var value) && value == CookieValue(language);

    private sealed class QueryStringLanguageProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            if (FromQuery(httpContext.Request) is not { } language) return NullProviderCultureResult;
            var name = CultureFor(language).Name;
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(name, name));
        }
    }

    public static string CookieValue(string language)
    {
        var culture = CultureFor(language);
        return CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture));
    }
}
