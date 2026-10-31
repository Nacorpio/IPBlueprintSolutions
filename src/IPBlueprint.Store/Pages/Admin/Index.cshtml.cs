using IPBlueprint.Store.Data;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages.Admin;

public class IndexModel(StoreDbContext db, IOptions<SumUpOptions> sumUp, TimeProvider clock) : PageModel
{
    public int ToShip { get; private set; }
    public int NewRequests { get; private set; }
    public decimal Sales30Days { get; private set; }
    public int ActiveProducts { get; private set; }
    public bool SumUpConfigured => sumUp.Value.IsConfigured;
    public List<Order> LatestOrders { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        ToShip = await db.Orders.CountAsync(o => OrderStatusFlow.ToDo.Contains(o.Status), ct);
        NewRequests = await db.CustomOrderRequests.CountAsync(r => r.Status == CustomRequestStatus.New, ct);
        ActiveProducts = await db.Products.CountAsync(p => p.IsActive, ct);

        // SQLite can't sum decimals in SQL, and the numbers are small, so sum in memory.
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        Sales30Days = (await db.Orders
                .Where(o => OrderStatusFlow.PaidStatuses.Contains(o.Status) && o.PaidAtUtc >= since)
                .Select(o => o.Total).ToListAsync(ct))
            .Sum();

        LatestOrders = await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAtUtc).Take(10).ToListAsync(ct);
    }
}
