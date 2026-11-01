using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

public class CheckoutModel(
    CartService cart,
    OrderService orders,
    IOptions<ShopOptions> shopOptions,
    IStringLocalizer<SharedResource> localizer,
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

        // Something sold out while they were filling in the form: show the new cart and total before taking payment.
        if (Summary.Adjustments.Count > 0) return Page();

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
            ErrorMessage = localizer["We couldn't reach the payment provider just now. Nothing has been charged. Please try again in a moment."];
            return Page();
        }
    }
}

/// <summary>Messages and labels are English keys; Swedish comes from Localization/SharedResource.sv.resx.</summary>
public class CheckoutInput
{
    [Required(ErrorMessage = "Please enter your name.")]
    [StringLength(100, ErrorMessage = "The name can be at most {1} characters.")]
    [Display(Name = "Full name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Please enter your email address.")]
    [EmailAddress(ErrorMessage = "That doesn't look like an email address.")]
    [StringLength(200, ErrorMessage = "The email address can be at most {1} characters.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Phone(ErrorMessage = "That doesn't look like a phone number.")]
    [StringLength(40, ErrorMessage = "The phone number can be at most {1} characters.")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Please enter your street address.")]
    [StringLength(150, ErrorMessage = "The address can be at most {1} characters.")]
    [Display(Name = "Street address")]
    public string AddressLine1 { get; set; } = "";

    [StringLength(150, ErrorMessage = "The address can be at most {1} characters.")]
    [Display(Name = "Apartment, c/o etc. (optional)")]
    public string? AddressLine2 { get; set; }

    [Required(ErrorMessage = "Please enter your postcode.")]
    [RegularExpression(@"^\s*\d{3}\s?\d{2}\s*$", ErrorMessage = "Enter a Swedish postcode, e.g. 123 45.")]
    [Display(Name = "Postcode")]
    public string PostalCode { get; set; } = "";

    [Required(ErrorMessage = "Please enter your city.")]
    [StringLength(80, ErrorMessage = "The city can be at most {1} characters.")]
    [Display(Name = "City")]
    public string City { get; set; } = "";

    [StringLength(1000, ErrorMessage = "The note can be at most {1} characters.")]
    [Display(Name = "Anything we should know? (optional)")]
    public string? Note { get; set; }

    [Range(typeof(bool), "true", "true", ErrorMessage = "Please accept the terms to continue.")]
    public bool AcceptTerms { get; set; }
}
