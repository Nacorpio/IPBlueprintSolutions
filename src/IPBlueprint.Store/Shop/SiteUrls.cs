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
}
