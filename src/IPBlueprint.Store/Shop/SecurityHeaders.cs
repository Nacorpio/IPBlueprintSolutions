namespace IPBlueprint.Store.Shop;

/// <summary>
/// Browser protections sent with every response: no guessing file types (an uploaded file can't be run as a script),
/// no showing the shop inside another site's frame (click-jacking), and only the site's own address sent on to other
/// sites when a visitor follows a link. There is no script-src policy, because the admin pages use small inline scripts.
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((http, next) =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            return next(http);
        });
}
