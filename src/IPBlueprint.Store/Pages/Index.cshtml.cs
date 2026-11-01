using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages;

public class IndexModel(StoreDbContext db) : PageModel
{
    public List<Product> Products { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
    }
}
