using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IPBlueprint.Store.Pages.Admin.Requests;

public class DetailsModel(StoreDbContext db, FileStore files) : PageModel
{
    public CustomOrderRequest CustomRequest { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var r = await db.CustomOrderRequests.FindAsync([id], ct);
        if (r is null) return NotFound();
        CustomRequest = r;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CustomRequestStatus status, string? notes, CancellationToken ct)
    {
        var r = await db.CustomOrderRequests.FindAsync([id], ct);
        if (r is null) return NotFound();

        r.Status = status;
        r.AdminNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync(ct);

        TempData["Flash"] = "Saved.";
        return RedirectToPage(new { id });
    }

    /// <summary>Deletes the request and its file for good, for example when the customer asks to be forgotten.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var r = await db.CustomOrderRequests.FindAsync([id], ct);
        if (r is null) return NotFound();

        db.CustomOrderRequests.Remove(r);
        await db.SaveChangesAsync(ct);
        files.DeleteAttachment(r.AttachmentFileName);

        TempData["Flash"] = $"Deleted the request from {r.Name}.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnGetAttachmentAsync(int id, CancellationToken ct)
    {
        var r = await db.CustomOrderRequests.FindAsync([id], ct);
        var path = files.AttachmentPath(r?.AttachmentFileName);
        if (r is null || path is null || !System.IO.File.Exists(path)) return NotFound();

        // Always download, never display inline: the file came from a stranger.
        return PhysicalFile(path, "application/octet-stream", r.AttachmentOriginalName ?? r.AttachmentFileName);
    }
}
