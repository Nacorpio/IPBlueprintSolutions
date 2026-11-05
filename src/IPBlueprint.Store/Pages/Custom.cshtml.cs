using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Pages;

[EnableRateLimiting(FormRateLimits.CustomRequest)]
[RequestSizeLimit(FileStore.MaxAttachmentBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FileStore.MaxAttachmentBytes + 1024 * 1024)]
public class CustomModel(
    StoreDbContext db,
    FileStore files,
    OrderNotifications notifications,
    IOptions<ShopOptions> shop,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public CustomInput Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Attachment { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync([FromForm(Name = "website")] string? honeypot, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(honeypot))
        {
            // Pretend it worked so the bot moves on.
            TempData["Flash"] = localizer["Thanks! We've received your request and will get back to you soon."].Value;
            return RedirectToPage("/Index");
        }

        if (!ModelState.IsValid) return Page();

        string? storedName = null;
        if (Attachment is { Length: > 0 })
        {
            var (name, error) = await files.SaveAttachmentAsync(Attachment, ct);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(Attachment), error);
                return Page();
            }
            storedName = name;
        }

        var request = new CustomOrderRequest
        {
            Name = Input.Name.Trim(),
            Email = Input.Email.Trim(),
            Description = Input.Description.Trim(),
            PreferredColour = Input.PreferredColour?.Trim(),
            Budget = Input.Budget?.Trim(),
            Delivery = Input.Delivery == DeliveryMethod.Pickup && shop.Value.OffersPickup ? DeliveryMethod.Pickup : DeliveryMethod.Shipping,
            AttachmentFileName = storedName,
            AttachmentOriginalName = storedName is null ? null : Path.GetFileName(Attachment!.FileName),
            Language = Languages.Current,
        };
        db.CustomOrderRequests.Add(request);
        await db.SaveChangesAsync(ct);

        await notifications.CustomRequestReceivedAsync(request);

        TempData["Flash"] = localizer["Thanks! We've received your request and sent you a confirmation by email. We'll get back to you soon."].Value;
        return RedirectToPage("/Index");
    }
}

public class CustomInput
{
    [Required(ErrorMessage = "Please enter your name.")]
    [StringLength(100, ErrorMessage = "The name can be at most {1} characters.")]
    [Display(Name = "Your name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Please enter your email address.")]
    [EmailAddress(ErrorMessage = "That doesn't look like an email address.")]
    [StringLength(200, ErrorMessage = "The email address can be at most {1} characters.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Please tell us what you'd like us to print.")]
    [StringLength(4000, MinimumLength = 10, ErrorMessage = "Please write between {2} and {1} characters.")]
    [Display(Name = "What would you like?")]
    public string Description { get; set; } = "";

    [StringLength(100, ErrorMessage = "This can be at most {1} characters.")]
    [Display(Name = "Preferred colour (optional)")]
    public string? PreferredColour { get; set; }

    [StringLength(100, ErrorMessage = "This can be at most {1} characters.")]
    [Display(Name = "Budget (optional)")]
    public string? Budget { get; set; }

    public DeliveryMethod Delivery { get; set; } = DeliveryMethod.Shipping;
}
