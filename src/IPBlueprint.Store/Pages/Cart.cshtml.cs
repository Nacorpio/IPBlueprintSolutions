using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

public class CartModel(CartService cart, IOptions<ShopOptions> options) : PageModel
{
    public CartSummary Summary { get; private set; } = null!;
    public ShopOptions Shop => options.Value;

    public async Task OnGetAsync(CancellationToken ct) => Summary = await cart.GetSummaryAsync(ct);

    public IActionResult OnPostUpdate(int productId, int quantity)
    {
        cart.SetQuantity(productId, quantity);
        return RedirectToPage();
    }
}
