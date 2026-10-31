using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Shop;

public enum StockAlertResult
{
    Added,
    AlreadyAdded,
    InvalidEmail,
    /// <summary>The product isn't sold out (any more), so there's nothing to wait for.</summary>
    NotSoldOut,
    /// <summary>Too many people are already waiting for this product. Stops someone filling the table with junk.</summary>
    Full,
}

/// <summary>
/// "Email me when it's back": visitors leave their email on a sold-out product, and get one email when it's in stock
/// again (or switched to made to order). The address is deleted as soon as that email has gone out.
/// </summary>
public sealed class StockAlerts(StoreDbContext db, OrderNotifications notifications, TimeProvider clock, ILogger<StockAlerts> logger)
{
    public const int MaxPerProduct = 500;

    private static readonly EmailAddressAttribute EmailCheck = new();

    public async Task<StockAlertResult> SubscribeAsync(Product product, string? email, string language, CancellationToken ct = default)
    {
        email = email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 200 || !EmailCheck.IsValid(email)) return StockAlertResult.InvalidEmail;
        if (!product.IsSoldOut) return StockAlertResult.NotSoldOut;

        var normalized = email.ToLowerInvariant();
        if (await db.StockAlerts.AnyAsync(a => a.ProductId == product.Id && a.Email == normalized, ct)) return StockAlertResult.AlreadyAdded;
        if (await db.StockAlerts.CountAsync(a => a.ProductId == product.Id, ct) >= MaxPerProduct) return StockAlertResult.Full;

        db.StockAlerts.Add(new StockAlert
        {
            ProductId = product.Id,
            Email = normalized,
            Language = Languages.Normalize(language),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two clicks at once: the unique index caught the second one.
            return StockAlertResult.AlreadyAdded;
        }
        return StockAlertResult.Added;
    }

    /// <summary>Emails everyone waiting for the product if it can be bought again, then forgets them.</summary>
    public async Task<int> NotifyIfAvailableAsync(int productId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || !product.IsActive || product.IsSoldOut) return 0;

        var alerts = await db.StockAlerts.Where(a => a.ProductId == productId).ToListAsync(ct);
        if (alerts.Count == 0) return 0;

        // Forget the addresses first, so a second save of the product can't email the same people again.
        var ids = alerts.Select(a => a.Id).ToList();
        await db.StockAlerts.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);

        foreach (var alert in alerts) await notifications.BackInStockAsync(product, alert, ct);

        logger.LogInformation("{Product} is back in stock; emailed {Count} people who asked", product.Name, alerts.Count);
        return alerts.Count;
    }

    public Task<int> CountWaitingAsync(int productId, CancellationToken ct = default) =>
        db.StockAlerts.CountAsync(a => a.ProductId == productId, ct);
}
