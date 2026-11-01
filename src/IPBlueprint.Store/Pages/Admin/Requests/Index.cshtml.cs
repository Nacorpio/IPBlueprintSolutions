using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Requests;

public class IndexModel(StoreDbContext db) : PageModel
{
    public List<CustomOrderRequest> Requests { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Requests = await db.CustomOrderRequests.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).Take(500).ToListAsync(ct);
}
