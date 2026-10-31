using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Orders;

public class DetailsModel(StoreDbContext db, OrderNotifications notifications, TimeProvider clock) : PageModel
{
    public Order Order { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();
        Order = order;
        return Page();
    }

    public async Task<IActionResult> OnPostShipAsync(Guid id, string? trackingNumber, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();
        if (order.Status != OrderStatus.Paid) return RedirectToPage(new { id });

        order.Status = OrderStatus.Shipped;
        order.ShippedAtUtc = clock.GetUtcNow().UtcDateTime;
        order.TrackingNumber = string.IsNullOrWhiteSpace(trackingNumber) ? null : trackingNumber.Trim();
        await db.SaveChangesAsync(ct);
        await notifications.OrderShippedAsync(order, ct);

        TempData["Flash"] = $"Order {order.Reference} marked as shipped. The customer has been emailed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();
        if (order.Status is OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed)
        {
            order.Status = OrderStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            TempData["Flash"] = $"Order {order.Reference} cancelled.";
        }
        return RedirectToPage(new { id });
    }

    private Task<Order?> Load(Guid id, CancellationToken ct) =>
        db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
}
