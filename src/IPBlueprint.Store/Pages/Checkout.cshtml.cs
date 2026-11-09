using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

[EnableRateLimiting(FormRateLimits.Checkout)]
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

    public ShopOptions Shop => shopOptions.Value;

    /// <summary>The cart priced for each way of getting it, so the page can show both totals and switch between them.</summary>
    public CartSummary ForDelivery(DeliveryMethod delivery) => Summary.WithDelivery(delivery, Shop);

    /// <summary>The address fields, which a pickup order doesn't need.</summary>
    private static readonly string[] AddressFields =
        [nameof(CheckoutInput.AddressLine1), nameof(CheckoutInput.AddressLine2), nameof(CheckoutInput.PostalCode), nameof(CheckoutInput.City)];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Summary = await cart.GetSummaryAsync(ct);
        return Summary.IsEmpty ? RedirectToPage("/Cart") : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Summary = await cart.GetSummaryAsync(ct);
        if (Summary.IsEmpty) return RedirectToPage("/Cart");

        // Pickup switched off since the page was loaded (or a made-up form): fall back to shipping, which needs the address.
        if (Input.Delivery == DeliveryMethod.Pickup && !Shop.OffersPickup) Input.Delivery = DeliveryMethod.Shipping;
        if (!Enum.IsDefined(Input.Delivery)) Input.Delivery = DeliveryMethod.Shipping;

        if (Input.Delivery == DeliveryMethod.Pickup)
        {
            foreach (var field in AddressFields) ModelState.Remove($"{nameof(Input)}.{field}");
        }

        Summary = Summary.WithDelivery(Input.Delivery, Shop);
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
            return paymentUrl is null ? RedirectToPage("/Order", new { id = order.Id }) : Redirect(paymentUrl);
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
    /// <summary>Sent by post, or picked up from us for free. The address is only required when it's sent.</summary>
    public DeliveryMethod Delivery { get; set; } = DeliveryMethod.Shipping;

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
