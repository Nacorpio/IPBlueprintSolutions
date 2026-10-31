using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Categories;

public class IndexModel(StoreDbContext db) : PageModel
{
    public sealed record Row(Category Category, int ProductCount);

    public List<Row> Categories { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new Row(c, c.Products.Count))
            .ToListAsync(ct);
}
