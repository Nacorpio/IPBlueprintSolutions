using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages;

public class GalleryModel(StoreDbContext db) : PageModel
{
    public List<GalleryImage> Images { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Images = await db.GalleryImages.AsNoTracking().OrderBy(g => g.SortOrder).ThenByDescending(g => g.Id).ToListAsync(ct);
}
