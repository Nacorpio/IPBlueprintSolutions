using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages.Admin.Orders;

public class DetailsModel(StoreDbContext db, OrderService orders, OrderNotifications notifications, IOptions<ShopOptions> shop) : PageModel
{
    public string Town => shop.Value.Town;

    public Order Order { get; private set; } = null!;

    /// <summary>The customer's private order page, to copy into a reply if they ask.</summary>
    public string? StatusUrl { get; private set; }

    /// <summary>True when some of the items came out of stock, so a refund can put them back.</summary>
    public bool HasStockedItems { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.History.OrderBy(h => h.AtUtc))
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();

        Order = order;
        StatusUrl = notifications.StatusUrl(order);
        var productIds = order.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToList();
        HasStockedItems = await db.Products.AnyAsync(p => productIds.Contains(p.Id) && p.Stock != null, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostStatusAsync(Guid id, OrderStatus to, string? trackingNumber, bool emailCustomer, bool restock, CancellationToken ct)
    {
        var result = await orders.ChangeStatusAsync(id, new StatusChangeRequest(to, trackingNumber, emailCustomer, restock), ct);
        if (result == StatusChangeResult.NotFound) return NotFound();

        var delivery = await db.Orders.Where(o => o.Id == id).Select(o => o.Delivery).FirstOrDefaultAsync(ct);
        var label = OrderStatusFlow.AdminLabel(to, delivery).ToLowerInvariant();
        TempData["Flash"] = result == StatusChangeResult.Changed
            ? $"Marked as {label}." + (emailCustomer && OrderStatusFlow.EmailsCustomer(to) ? " The customer has been emailed." : "")
            : "That change isn't possible from the order's current status. Someone may have just changed it; here is how it looks now.";
        return RedirectToPage(new { id });
    }
}
