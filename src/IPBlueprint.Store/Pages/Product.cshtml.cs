using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

public class ProductModel(StoreDbContext db, CartService cart, IOptions<ShopOptions> options) : PageModel
{
    public Product Product { get; private set; } = null!;
    public int MaxQuantity => options.Value.MaxQuantityPerItem;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var product = await Find(slug, ct);
        if (product is null) return NotFound();
        Product = product;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, int quantity, CancellationToken ct)
    {
        var product = await Find(slug, ct);
        if (product is null) return NotFound();

        cart.Add(product.Id, Math.Max(1, quantity));
        TempData["Flash"] = $"{product.Name} was added to your cart.";
        return RedirectToPage("/Cart");
    }

    private Task<Product?> Find(string slug, CancellationToken ct) =>
        db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);
}
