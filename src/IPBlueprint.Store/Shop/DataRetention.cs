using IPBlueprint.Store.Data;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Deletes personal data the shop has no reason to keep, as the privacy page promises: abandoned orders (never paid,
/// 30 days after the last attempt to pay) and custom requests that didn't lead to an order (after 12 months), with
/// their attached files. Paid orders are kept, because bookkeeping law needs them.
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

        // The clock starts at the last attempt to pay, so an order whose customer pressed "Pay now" yesterday keeps its
        // open SumUp checkout. An order with a payment issue stays until the owners resolve it, and 30 days after that.
        var orderCutoff = now - UnpaidOrderAge;
        var orders = await db.Orders
            .Where(o => NeverPaid.Contains(o.Status)
                        && (o.LastPaymentAttemptAtUtc ?? o.CreatedAtUtc) < orderCutoff
                        && o.PaymentIssues.All(i => i.ResolvedAtUtc != null && i.ResolvedAtUtc < orderCutoff))
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
