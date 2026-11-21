using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Every rate limit in the shop, per visitor: the admin login, the public forms that send email or start a payment,
/// and the SumUp webhook. Only posts count on the forms, so viewing a page is never limited. Without these, a script
/// could guess the admin password, or use the forms to send email from the shop's account to anyone, over and over.
/// </summary>
public static class RateLimits
{
    public const string AdminLogin = "admin-login";
    public const string CustomRequest = "custom-request";
    public const string Checkout = "checkout";

    /// <summary>"Email me when it's back" on a product page. Adding to the cart on the same page is not limited.</summary>
    public const string StockAlert = "stock-alert";

    /// <summary>Each webhook call makes the shop ask SumUp's API, so a flood of made-up calls could use up SumUp's limit.</summary>
    public const string SumUpWebhook = "sumup-webhook";

    public static void AddStoreRateLimits(this RateLimiterOptions options)
    {
        options.AddPolicy(AdminLogin, ctx => PerVisitor(ctx, AdminLogin, permits: 10, TimeSpan.FromMinutes(5)));
        options.AddPolicy(SumUpWebhook, ctx => PerVisitor(ctx, SumUpWebhook, permits: 60, TimeSpan.FromMinutes(1)));
        options.AddPolicy(CustomRequest, ctx => PostsPerVisitor(ctx, CustomRequest, permits: 5, TimeSpan.FromHours(1)));
        options.AddPolicy(Checkout, ctx => PostsPerVisitor(ctx, Checkout, permits: 10, TimeSpan.FromMinutes(10)));
        // Razor Pages runs the handler named by the first "handler" value, so look at each value rather than the joined
        // string: "?handler=Notify&handler=x" reads as "Notify,x" but still runs OnPostNotify.
        options.AddPolicy(StockAlert, ctx =>
            ctx.Request.Query["handler"].Any(h => string.Equals(h, "notify", StringComparison.OrdinalIgnoreCase))
                ? PostsPerVisitor(ctx, StockAlert, permits: 5, TimeSpan.FromHours(1))
                : RateLimitPartition.GetNoLimiter(StockAlert + ":other"));
    }

    /// <summary>
    /// Who counts as one visitor: the IPv4 address, or the /64 network of an IPv6 address. One IPv6 connection
    /// usually comes with a whole /64 (billions of addresses), so counting single IPv6 addresses would let anyone
    /// get around every limit by changing address on each request.
    /// </summary>
    public static string VisitorKey(HttpContext ctx) => VisitorKey(ctx.Connection.RemoteIpAddress);

    public static string VisitorKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static RateLimitPartition<string> PostsPerVisitor(HttpContext ctx, string policy, int permits, TimeSpan window) =>
        HttpMethods.IsPost(ctx.Request.Method)
            ? PerVisitor(ctx, policy, permits, window)
            : RateLimitPartition.GetNoLimiter(policy + ":read");

    private static RateLimitPartition<string> PerVisitor(HttpContext ctx, string policy, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{VisitorKey(ctx)}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });
}
