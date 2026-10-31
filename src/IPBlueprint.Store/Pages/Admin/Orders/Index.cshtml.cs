using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Orders;

public class IndexModel(StoreDbContext db) : PageModel
{
    /// <summary>"todo" (paid or in production), a status name, or empty for everything except unpaid orders.</summary>
    public string? Filter { get; private set; }
    public List<Order> Orders { get; private set; } = [];

    public async Task OnGetAsync(string? status, CancellationToken ct)
    {
        Filter = status;
        var query = db.Orders.AsNoTracking();
        if (status == "todo")
            query = query.Where(o => OrderStatusFlow.ToDo.Contains(o.Status));
        else if (Enum.TryParse<OrderStatus>(status, out var s))
            query = query.Where(o => o.Status == s);
        else
            query = query.Where(o => o.Status != OrderStatus.AwaitingPayment && o.Status != OrderStatus.PaymentFailed);
        Orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(500).ToListAsync(ct);
    }
}
