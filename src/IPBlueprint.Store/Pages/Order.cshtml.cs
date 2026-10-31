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

/// <summary>Where SumUp sends the customer after paying. The order id is a random GUID, so the link itself is the key.</summary>
public class OrderModel(
    StoreDbContext db,
    OrderService orders,
    IOptions<ShopOptions> shopOptions,
    IStringLocalizer<SharedResource> localizer,
    ILogger<OrderModel> logger) : PageModel
{
    public Order Order { get; private set; } = null!;

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
        return Page();
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
        db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
}
