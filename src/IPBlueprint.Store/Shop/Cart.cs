using IPBlueprint.Store.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Shop;

public sealed record CartLine(Product Product, int Quantity)
{
    public decimal LineTotal => Product.Price * Quantity;
}

/// <summary>A cart line we had to shrink or drop because there isn't enough in stock.</summary>
public sealed record CartAdjustment(Product Product, int Requested, int Available);

public sealed record CartSummary(IReadOnlyList<CartLine> Lines, decimal Subtotal, decimal Shipping)
{
    /// <summary>Lines changed because of stock since the customer added them. The cart already reflects the change.</summary>
    public IReadOnlyList<CartAdjustment> Adjustments { get; init; } = [];

    public decimal Total => Subtotal + Shipping;
    public bool IsEmpty => Lines.Count == 0;
    public int ItemCount => Lines.Sum(l => l.Quantity);
}

public static class Pricing
{
    public static decimal ShippingFor(decimal subtotal, ShopOptions options)
    {
        if (subtotal <= 0) return 0;
        if (options.FreeShippingFrom > 0 && subtotal >= options.FreeShippingFrom) return 0;
        return options.ShippingFlatRate;
    }
}

/// <summary>
/// The cart lives in a cookie as "productId:quantity" pairs. Prices are always looked up in the database,
/// so a tampered cookie can only change what is in the cart, never what it costs.
/// </summary>
public sealed class CartService(IHttpContextAccessor http, StoreDbContext db, IOptions<ShopOptions> options)
{
    private const string CookieName = "cart";
    private const int MaxDistinctItems = 50;

    private HttpContext Context => http.HttpContext ?? throw new InvalidOperationException("No active request.");

    public Dictionary<int, int> ReadRaw()
    {
        var result = new Dictionary<int, int>();
        if (!Context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(MaxDistinctItems))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var qty) && qty > 0)
                result[id] = Math.Min(qty, options.Value.MaxQuantityPerItem);
        }

        return result;
    }

    /// <summary>Adds the product, never going over what's in stock. Returns how many are now in the cart (0 if sold out).</summary>
    public int Add(Product product, int quantity)
    {
        var available = product.AvailableToBuy(options.Value.MaxQuantityPerItem);
        if (available <= 0) return 0;

        var cart = ReadRaw();
        cart[product.Id] = Math.Clamp(cart.GetValueOrDefault(product.Id) + quantity, 1, available);
        Write(cart);
        return cart[product.Id];
    }

    public void SetQuantity(int productId, int quantity)
    {
        var cart = ReadRaw();
        if (quantity <= 0) cart.Remove(productId);
        else cart[productId] = Math.Min(quantity, options.Value.MaxQuantityPerItem);
        Write(cart);
    }

    public void Clear() => Context.Response.Cookies.Delete(CookieName);

    public int CountItems() => ReadRaw().Values.Sum();

    public async Task<CartSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var raw = ReadRaw();
        if (raw.Count == 0) return new CartSummary([], 0, 0);

        var ids = raw.Keys.ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToListAsync(ct);

        // Stock may have run out since the customer put something in the cart: shrink or drop those lines, and say so.
        var lines = new List<CartLine>();
        var adjustments = new List<CartAdjustment>();
        foreach (var p in products.OrderBy(p => p.SortOrder).ThenBy(p => p.Name))
        {
            var requested = raw[p.Id];
            var available = p.AvailableToBuy(options.Value.MaxQuantityPerItem);
            if (requested > available) adjustments.Add(new CartAdjustment(p, requested, available));
            if (available > 0) lines.Add(new CartLine(p, Math.Min(requested, available)));
        }

        if (adjustments.Count > 0 || lines.Count != raw.Count)
            Write(lines.ToDictionary(l => l.Product.Id, l => l.Quantity));

        var subtotal = lines.Sum(l => l.LineTotal);
        return new CartSummary(lines, subtotal, Pricing.ShippingFor(subtotal, options.Value)) { Adjustments = adjustments };
    }

    private void Write(Dictionary<int, int> cart)
    {
        var value = string.Join(',', cart.Select(kv => $"{kv.Key}:{kv.Value}"));
        Context.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true, // needed for the shop to work, so no consent banner is required for it
            SameSite = SameSiteMode.Lax,
            Secure = Context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(14),
        });
    }
}
