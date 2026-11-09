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

    /// <summary>Emails everyone waiting for the product if it can be bought again, and forgets each address once its email
    /// has gone out. Returns how many were emailed.</summary>
    public async Task<int> NotifyIfAvailableAsync(int productId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || !product.IsActive || product.IsSoldOut) return 0;

        var alerts = await db.StockAlerts.Where(a => a.ProductId == productId).ToListAsync(ct);
        if (alerts.Count == 0) return 0;

        var sent = 0;
        foreach (var alert in alerts)
        {
            // Claim the address by deleting it before sending, so a second save of the product at the same time can't
            // email the same person again. From here on nothing may be cancelled, or the address could be lost unsent.
            var claimed = await db.StockAlerts.Where(a => a.Id == alert.Id).ExecuteDeleteAsync(CancellationToken.None);
            if (claimed == 0) continue;

            if (await notifications.BackInStockAsync(product, alert))
            {
                sent++;
                continue;
            }

            // Not sent (mail server down or rate limiting): put the address back so they hear about the next restock.
            await RestoreAsync(alert);
        }

        logger.LogInformation("{Product} is back in stock; emailed {Sent} of {Count} people who asked", product.Name, sent, alerts.Count);
        return sent;
    }

    private async Task RestoreAsync(StockAlert alert)
    {
        db.StockAlerts.Add(new StockAlert
        {
            ProductId = alert.ProductId,
            Email = alert.Email,
            Language = alert.Language,
            CreatedAtUtc = alert.CreatedAtUtc,
        });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // They signed up again in the meantime (already on the list), or the product was just deleted.
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Could not put a back-in-stock request for product {ProductId} back on the list", alert.ProductId);
        }
    }

    public Task<int> CountWaitingAsync(int productId, CancellationToken ct = default) =>
        db.StockAlerts.CountAsync(a => a.ProductId == productId, ct);
}
