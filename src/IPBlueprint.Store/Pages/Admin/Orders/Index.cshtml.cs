using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Orders;

public class IndexModel(StoreDbContext db) : PageModel
{
    public OrderStatus? Status { get; private set; }
    public List<Order> Orders { get; private set; } = [];

    public async Task OnGetAsync(OrderStatus? status, CancellationToken ct)
    {
        Status = status;
        var query = db.Orders.AsNoTracking();
        query = status is { } s
            ? query.Where(o => o.Status == s)
            : query.Where(o => o.Status != OrderStatus.AwaitingPayment && o.Status != OrderStatus.PaymentFailed);
        Orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(500).ToListAsync(ct);
    }
}
