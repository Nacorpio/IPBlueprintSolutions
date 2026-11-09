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

    /// <summary>Where pickup orders are collected.</summary>
    public string Town => shopOptions.Value.Town;

    /// <summary>Paid, in production, shipped, delivered, for paid orders. Empty otherwise.</summary>
    public IReadOnlyList<OrderStep> Steps { get; private set; } = [];

    /// <summary>The normal path of a paid order that is sent.</summary>
    private static readonly OrderStatus[] ShippingPath = [OrderStatus.Paid, OrderStatus.InProduction, OrderStatus.Shipped, OrderStatus.Delivered];

    /// <summary>The normal path of a paid order the customer picks up. Delivered is shown as "Picked up".</summary>
    private static readonly OrderStatus[] PickupPath = [OrderStatus.Paid, OrderStatus.InProduction, OrderStatus.ReadyForPickup, OrderStatus.Delivered];

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
        var path = order.IsPickup ? PickupPath : ShippingPath;
        var at = Array.IndexOf(path, order.Status);
        if (at < 0) return [];

        return path.Select((status, i) =>
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
        // A payment came in that the owners have to look at by hand (wrong amount): don't let the customer pay again.
        if (order.PaymentNeedsChecking) return RedirectToPage(new { id });

        try
        {
            // Make sure the previous attempt didn't go through after all, so the customer isn't charged twice.
            if (order.SumUpCheckoutId is { } previous &&
                await orders.SyncCheckoutAsync(previous, ct) is PaymentSyncResult.MarkedPaid or PaymentSyncResult.AlreadyPaid
                    or PaymentSyncResult.AmountMismatch)
            {
                return RedirectToPage(new { id });
            }

            await db.Entry(order).ReloadAsync(ct);
            var paymentUrl = await orders.StartPaymentAsync(order, SiteUrls.BaseUrl(Request, shopOptions.Value), ct);
            return paymentUrl is null ? RedirectToPage(new { id }) : Redirect(paymentUrl);
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
