using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Limits on the public forms that send email or start a payment, per visitor IP. Only posts count, so viewing the
/// form is never limited. Without these, a script could use the custom order form to send email from the shop's
/// account to anyone, over and over.
/// </summary>
public static class FormRateLimits
{
    public const string CustomRequest = "custom-request";
    public const string Checkout = "checkout";

    public static void AddFormRateLimits(this RateLimiterOptions options)
    {
        options.AddPolicy(CustomRequest, ctx => PostsPerIp(ctx, CustomRequest, permits: 5, TimeSpan.FromHours(1)));
        options.AddPolicy(Checkout, ctx => PostsPerIp(ctx, Checkout, permits: 10, TimeSpan.FromMinutes(10)));
    }

    private static RateLimitPartition<string> PostsPerIp(HttpContext ctx, string policy, int permits, TimeSpan window) =>
        HttpMethods.IsPost(ctx.Request.Method)
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{policy}:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window })
            : RateLimitPartition.GetNoLimiter(policy + ":read");
}
