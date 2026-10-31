using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

public class ProductModel(
    StoreDbContext db,
    CartService cart,
    StockAlerts stockAlerts,
    IOptions<ShopOptions> options,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public Product Product { get; private set; } = null!;
    public int MaxQuantity => Product.AvailableToBuy(options.Value.MaxQuantityPerItem);

    /// <summary>The answer after someone asked to be told when the product is back.</summary>
    public string? AlertMessage { get; private set; }

    public string? AlertError { get; private set; }

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

        var inCart = cart.Add(product, Math.Max(1, quantity));
        if (inCart == 0)
        {
            TempData["Flash"] = localizer["Sorry, {0} has just sold out.", product.DisplayName].Value;
            return RedirectToPage(new { slug });
        }

        TempData["Flash"] = localizer["{0} was added to your cart.", product.DisplayName].Value;
        return RedirectToPage("/Cart");
    }

    public async Task<IActionResult> OnPostNotifyAsync(string slug, string? email, CancellationToken ct)
    {
        var product = await Find(slug, ct);
        if (product is null) return NotFound();
        Product = product;

        switch (await stockAlerts.SubscribeAsync(product, email, Languages.Current, ct))
        {
            case StockAlertResult.InvalidEmail:
                AlertError = localizer["That doesn't look like an email address."];
                break;
            case StockAlertResult.NotSoldOut:
                break; // it came back while they were looking; the page now shows "Add to cart"
            default:
                // "Full" also gets a thank you: nothing to gain from telling a stranger how the list works.
                AlertMessage = localizer["Thanks! We'll email {0} when it's back.", email!.Trim()];
                break;
        }
        return Page();
    }

    private Task<Product?> Find(string slug, CancellationToken ct) =>
        db.Products.AsNoTracking().Include(p => p.Categories.OrderBy(c => c.SortOrder))
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);
}
