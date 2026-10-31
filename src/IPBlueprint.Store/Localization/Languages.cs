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
        o.RequestCultureProviders = [new CookieRequestCultureProvider()];
    }

    public static string CookieValue(string language)
    {
        var culture = CultureFor(language);
        return CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture));
    }
}
