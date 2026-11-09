using IPBlueprint.Store.Data;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Deletes personal data the shop has no reason to keep, as the privacy page promises: orders that were never paid
/// (after 30 days) and custom requests that didn't lead to an order (after 12 months), with their attached files.
/// Paid orders are kept, because bookkeeping law needs them.
/// </summary>
public sealed class DataRetention(StoreDbContext db, FileStore files, TimeProvider clock, ILogger<DataRetention> logger)
{
    public static readonly TimeSpan UnpaidOrderAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan OpenRequestAge = TimeSpan.FromDays(365);

    private static readonly OrderStatus[] NeverPaid = [OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed, OrderStatus.Cancelled];
    private static readonly CustomRequestStatus[] NoOrder = [CustomRequestStatus.New, CustomRequestStatus.Quoted, CustomRequestStatus.Declined];

    public async Task CleanUpAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // An order with a stray payment on it (wrong amount, or paid after it was cancelled) stays until it's sorted out.
        var orderCutoff = now - UnpaidOrderAge;
        var orders = await db.Orders
            .Where(o => NeverPaid.Contains(o.Status) && o.CreatedAtUtc < orderCutoff && o.ExtraPaymentCheckoutId == null)
            .ExecuteDeleteAsync(ct);

        var requestCutoff = now - OpenRequestAge;
        var oldRequests = await db.CustomOrderRequests
            .Where(r => NoOrder.Contains(r.Status) && r.CreatedAtUtc < requestCutoff)
            .ToListAsync(ct);
        foreach (var request in oldRequests) files.DeleteAttachment(request.AttachmentFileName);
        db.CustomOrderRequests.RemoveRange(oldRequests);
        await db.SaveChangesAsync(ct);

        if (orders > 0 || oldRequests.Count > 0)
            logger.LogInformation("Deleted {Orders} unpaid orders and {Requests} old custom requests", orders, oldRequests.Count);
    }
}

/// <summary>Runs <see cref="DataRetention"/> at startup and then once a day.</summary>
public sealed class DataRetentionJob(IServiceScopeFactory scopes, ILogger<DataRetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DataRetention>().CleanUpAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not delete old personal data; will try again tomorrow");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
