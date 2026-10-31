using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

public class CheckoutModel(
    CartService cart,
    OrderService orders,
    IOptions<ShopOptions> shopOptions,
    ILogger<CheckoutModel> logger) : PageModel
{
    [BindProperty]
    public CheckoutInput Input { get; set; } = new();

    public CartSummary Summary { get; private set; } = null!;
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Summary = await cart.GetSummaryAsync(ct);
        return Summary.IsEmpty ? RedirectToPage("/Cart") : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Summary = await cart.GetSummaryAsync(ct);
        if (Summary.IsEmpty) return RedirectToPage("/Cart");
        if (!ModelState.IsValid) return Page();

        var order = await orders.CreateOrderAsync(Summary, new CustomerDetails(
            Input.Name, Input.Email, Input.Phone, Input.AddressLine1, Input.AddressLine2,
            Input.PostalCode, Input.City, Input.Note), ct);

        try
        {
            var paymentUrl = await orders.StartPaymentAsync(order, SiteUrls.BaseUrl(Request, shopOptions.Value), ct);
            cart.Clear();
            return Redirect(paymentUrl);
        }
        catch (Exception ex) when (ex is SumUpException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not start payment for order {Reference}", order.Reference);
            ErrorMessage = "We couldn't reach the payment provider just now. Nothing has been charged. Please try again in a moment.";
            return Page();
        }
    }
}

public class CheckoutInput
{
    [Required, StringLength(100), Display(Name = "Full name")]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(200), Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Phone, StringLength(40), Display(Name = "Phone")]
    public string? Phone { get; set; }

    [Required, StringLength(150), Display(Name = "Street address")]
    public string AddressLine1 { get; set; } = "";

    [StringLength(150), Display(Name = "Apartment, c/o etc. (optional)")]
    public string? AddressLine2 { get; set; }

    [Required, RegularExpression(@"^\s*\d{3}\s?\d{2}\s*$", ErrorMessage = "Enter a Swedish postcode, e.g. 123 45."), Display(Name = "Postcode")]
    public string PostalCode { get; set; } = "";

    [Required, StringLength(80), Display(Name = "City")]
    public string City { get; set; } = "";

    [StringLength(1000), Display(Name = "Anything we should know? (optional)")]
    public string? Note { get; set; }

    [Range(typeof(bool), "true", "true", ErrorMessage = "Please accept the terms to continue.")]
    public bool AcceptTerms { get; set; }
}
