namespace IPBlueprint.Store.Shop;

public static class SiteUrls
{
    /// <summary>The site's public base URL, always ending in "/". Prefers Shop:PublicBaseUrl so links sent to SumUp
    /// are right even when the request came through a proxy or a different host name.</summary>
    public static Uri BaseUrl(HttpRequest request, ShopOptions options)
    {
        var configured = options.PublicBaseUrl?.Trim();
        var raw = string.IsNullOrEmpty(configured) ? $"{request.Scheme}://{request.Host}{request.PathBase}" : configured;
        return new Uri(raw.EndsWith('/') ? raw : raw + "/");
    }

    /// <summary>True for a path on this site ("/cart"), false for anything that could leave it ("//evil.test", "https://...").</summary>
    public static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' &&
        (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
