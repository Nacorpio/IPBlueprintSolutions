using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Limits on the public forms that send email or start a payment, per visitor. Only posts count, so viewing the
/// form is never limited. Without these, a script could use the custom order form to send email from the shop's
/// account to anyone, over and over.
/// </summary>
public static class FormRateLimits
{
    public const string CustomRequest = "custom-request";
    public const string Checkout = "checkout";

    /// <summary>"Email me when it's back" on a product page. Adding to the cart on the same page is not limited.</summary>
    public const string StockAlert = "stock-alert";

    public static void AddFormRateLimits(this RateLimiterOptions options)
    {
        options.AddPolicy(CustomRequest, ctx => PostsPerVisitor(ctx, CustomRequest, permits: 5, TimeSpan.FromHours(1)));
        options.AddPolicy(Checkout, ctx => PostsPerVisitor(ctx, Checkout, permits: 10, TimeSpan.FromMinutes(10)));
        options.AddPolicy(StockAlert, ctx =>
            string.Equals(ctx.Request.Query["handler"], "notify", StringComparison.OrdinalIgnoreCase)
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
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{policy}:{VisitorKey(ctx)}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window })
            : RateLimitPartition.GetNoLimiter(policy + ":read");
}
