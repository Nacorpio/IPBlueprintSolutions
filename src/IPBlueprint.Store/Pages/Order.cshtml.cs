using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

/// <summary>One step of the progress list on the order page.</summary>
public sealed record OrderStep(OrderStatus Status, bool Done, bool Current, DateTime? AtUtc);

/// <summary>
/// Where SumUp sends the customer after paying, and the private status page every order email links to. The order id
/// is a random GUID, so the link itself is the key; no account needed.
/// </summary>
public class OrderModel(
    StoreDbContext db,
    OrderService orders,
    IOptions<ShopOptions> shopOptions,
    IStringLocalizer<SharedResource> localizer,
    ILogger<OrderModel> logger) : PageModel
{
    public Order Order { get; private set; } = null!;

    /// <summary>Paid, in production, shipped, delivered, for paid orders. Empty otherwise.</summary>
    public IReadOnlyList<OrderStep> Steps { get; private set; } = [];

    /// <summary>The normal path of a paid order.</summary>
    private static readonly OrderStatus[] Path = [OrderStatus.Paid, OrderStatus.InProduction, OrderStatus.Shipped, OrderStatus.Delivered];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();

        // The webhook may not have arrived yet, so ask SumUp directly while the customer waits.
        if (order.Status == OrderStatus.AwaitingPayment && order.SumUpCheckoutId is { } checkoutId)
        {
            try
            {
                await orders.SyncCheckoutAsync(checkoutId, ct);
                db.ChangeTracker.Clear();
                order = (await Load(id, ct))!;
            }
            catch (Exception ex) when (ex is SumUpException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not check payment status for order {Reference}", order.Reference);
            }
        }

        Order = order;
        Steps = BuildSteps(order);
        return Page();
    }

    internal static IReadOnlyList<OrderStep> BuildSteps(Order order)
    {
        var at = Array.IndexOf(Path, order.Status);
        if (at < 0) return [];

        return Path.Select((status, i) =>
        {
            // Orders from before the history was kept still have their paid and shipped times.
            var when = order.History.Where(h => h.Status == status).Select(h => (DateTime?)h.AtUtc).LastOrDefault()
                       ?? (status == OrderStatus.Paid ? order.PaidAtUtc : status == OrderStatus.Shipped ? order.ShippedAtUtc : null);
            // A step that was skipped (shipped straight from paid) still counts as done, just without a date.
            return new OrderStep(status, i <= at, i == at, i <= at ? when : null);
        }).ToList();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();
        if (order.Status is not (OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed)) return RedirectToPage(new { id });

        try
        {
            // Make sure the previous attempt didn't go through after all, so the customer isn't charged twice.
            if (order.SumUpCheckoutId is { } previous &&
                await orders.SyncCheckoutAsync(previous, ct) is PaymentSyncResult.MarkedPaid or PaymentSyncResult.AlreadyPaid)
            {
                return RedirectToPage(new { id });
            }

            await db.Entry(order).ReloadAsync(ct);
            return Redirect(await orders.StartPaymentAsync(order, SiteUrls.BaseUrl(Request, shopOptions.Value), ct));
        }
        catch (Exception ex) when (ex is SumUpException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not restart payment for order {Reference}", order.Reference);
            TempData["Flash"] = localizer["We couldn't reach the payment provider. Please try again in a moment."].Value;
            return RedirectToPage(new { id });
        }
    }

    private Task<Order?> Load(Guid id, CancellationToken ct) =>
        db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.History).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);
}
