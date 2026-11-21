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
/// again (or switched to made to order). The emails go out from the background queue, and each address is deleted as
/// soon as its email has gone out, so nobody is lost if sending fails or the app restarts half way.
/// </summary>
public sealed class StockAlerts(
    StoreDbContext db,
    OrderNotifications notifications,
    BackgroundWorkQueue queue,
    TimeProvider clock,
    ILogger<StockAlerts> logger)
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
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Two clicks at once: the unique index caught the second one.
            return StockAlertResult.AlreadyAdded;
        }
        return StockAlertResult.Added;
    }

    /// <summary>
    /// After a product was saved or restocked: if it can be bought again and people are waiting, queues their emails and
    /// returns how many people will be emailed. The page that called this doesn't wait for the sending.
    /// </summary>
    public async Task<int> QueueNotifyIfAvailableAsync(int productId, CancellationToken ct = default)
    {
        var available = await db.Products.AnyAsync(p => p.Id == productId && p.IsActive && (p.Stock == null || p.Stock > 0), ct);
        if (!available) return 0;
        var waiting = await CountWaitingAsync(productId, ct);
        if (waiting > 0) queue.Enqueue((services, workCt) => services.GetRequiredService<StockAlerts>().NotifyIfAvailableAsync(productId, workCt));
        return waiting;
    }

    /// <summary>Emails everyone waiting for any product that can be bought again. Run at startup, to finish what a previous
    /// run (stopped half way through a list) didn't get to.</summary>
    public async Task NotifyAllAvailableAsync(CancellationToken ct = default)
    {
        var productIds = await db.StockAlerts.Select(a => a.ProductId).Distinct().ToListAsync(ct);
        foreach (var productId in productIds) await NotifyIfAvailableAsync(productId, ct);
    }

    /// <summary>Emails everyone waiting for the product if it can be bought again, and forgets each address once its email
    /// has gone out. Returns how many were emailed. Runs in the background queue: see <see cref="QueueNotifyIfAvailableAsync"/>.</summary>
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

            if (await notifications.SendBackInStockAsync(product, alert))
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
            // They signed up again in the meantime (already on the list), or the product was just deleted. Either way
            // there is nothing to put back, but anything else is worth seeing in the log.
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Could not put a back-in-stock request for product {ProductId} back on the list", alert.ProductId);
        }
    }

    public Task<int> CountWaitingAsync(int productId, CancellationToken ct = default) =>
        db.StockAlerts.CountAsync(a => a.ProductId == productId, ct);
}
