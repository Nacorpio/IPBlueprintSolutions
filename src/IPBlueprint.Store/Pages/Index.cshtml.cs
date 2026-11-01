using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IPBlueprint.Store.Pages;

public class IndexModel(Catalog catalog) : PageModel
{
    public CatalogView View { get; private set; } = null!;

    public async Task OnGetAsync(string? material, string? colour, string? sort, CancellationToken ct) =>
        View = (await catalog.BrowseAsync(null, material, colour, sort, ct))!;
}
