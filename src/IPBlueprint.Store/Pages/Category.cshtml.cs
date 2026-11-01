using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IPBlueprint.Store.Pages;

/// <summary>One category's own page, e.g. /categories/figurer. Listed in the sitemap.</summary>
public class CategoryModel(Catalog catalog) : PageModel
{
    public CatalogView View { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string slug, string? material, string? colour, string? sort, CancellationToken ct)
    {
        if (await catalog.BrowseAsync(slug, material, colour, sort, ct) is not { } view) return NotFound();
        View = view;
        return Page();
    }
}
