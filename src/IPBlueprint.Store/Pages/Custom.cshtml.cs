using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IPBlueprint.Store.Pages;

[RequestSizeLimit(FileStore.MaxAttachmentBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FileStore.MaxAttachmentBytes + 1024 * 1024)]
public class CustomModel(StoreDbContext db, FileStore files, OrderNotifications notifications) : PageModel
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
            TempData["Flash"] = "Thanks! We've received your request and will get back to you soon.";
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
            AttachmentFileName = storedName,
            AttachmentOriginalName = storedName is null ? null : Path.GetFileName(Attachment!.FileName),
        };
        db.CustomOrderRequests.Add(request);
        await db.SaveChangesAsync(ct);

        await notifications.CustomRequestReceivedAsync(request, ct);

        TempData["Flash"] = "Thanks! We've received your request and sent you a confirmation by email. We'll get back to you soon.";
        return RedirectToPage("/Index");
    }
}

public class CustomInput
{
    [Required, StringLength(100), Display(Name = "Your name")]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(200), Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Required, StringLength(4000, MinimumLength = 10), Display(Name = "What would you like?")]
    public string Description { get; set; } = "";

    [StringLength(100), Display(Name = "Preferred colour (optional)")]
    public string? PreferredColour { get; set; }

    [StringLength(100), Display(Name = "Budget (optional)")]
    public string? Budget { get; set; }
}
