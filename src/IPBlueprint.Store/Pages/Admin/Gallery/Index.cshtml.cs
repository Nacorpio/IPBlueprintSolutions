using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Gallery;

[RequestSizeLimit(GalleryPhotos.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = GalleryPhotos.MaxRequestBytes)]
public class IndexModel(StoreDbContext db, GalleryPhotos photos) : PageModel
{
    public const int MaxFilesPerUpload = 10;

    public List<GalleryImage> Images { get; private set; } = [];

    [BindProperty]
    public List<IFormFile> Photos { get; set; } = [];

    [BindProperty]
    public CaptionInput Upload { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        var files = Photos.Where(f => f.Length > 0).ToList();
        if (files.Count == 0) ModelState.AddModelError(nameof(Photos), "Choose at least one photo.");
        else if (files.Count > MaxFilesPerUpload) ModelState.AddModelError(nameof(Photos), $"Upload at most {MaxFilesPerUpload} photos at a time.");

        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        // New photos go first, in the order they were picked.
        var first = await db.GalleryImages.MinAsync(g => (int?)g.SortOrder, ct) ?? 0;
        var sortOrder = first - files.Count;

        var saved = new List<GalleryImage>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            var (photo, error) = await photos.SaveAsync(file, ct);
            if (photo is null)
            {
                errors.Add($"{Path.GetFileName(file.FileName)}: {error}");
                continue;
            }

            saved.Add(new GalleryImage
            {
                FileName = photo.FileName, Width = photo.Width, Height = photo.Height,
                ThumbnailFileName = photo.ThumbnailFileName, ThumbnailWidth = photo.ThumbnailWidth, ThumbnailHeight = photo.ThumbnailHeight,
                Caption = NullIfEmpty(Upload.Caption), CaptionEn = NullIfEmpty(Upload.CaptionEn),
                SortOrder = sortOrder++,
            });
        }

        db.GalleryImages.AddRange(saved);
        await db.SaveChangesAsync(ct);

        if (errors.Count > 0)
        {
            foreach (var e in errors) ModelState.AddModelError(nameof(Photos), e);
            if (saved.Count > 0) TempData["Flash"] = $"Added {Plural(saved.Count)}.";
            await LoadAsync(ct);
            return Page();
        }

        TempData["Flash"] = $"Added {Plural(saved.Count)} to the gallery.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, [Bind(Prefix = "Edit")] CaptionInput caption, CancellationToken ct)
    {
        var image = await db.GalleryImages.FindAsync([id], ct);
        if (image is null) return NotFound();

        // Each photo has its own small form, so only this photo's fields are validated here.
        if (!TryValidateModel(caption))
        {
            TempData["Flash"] = "Captions can be at most 300 characters.";
            return RedirectToPage();
        }

        image.Caption = NullIfEmpty(caption.Caption);
        image.CaptionEn = NullIfEmpty(caption.CaptionEn);
        await db.SaveChangesAsync(ct);

        TempData["Flash"] = "Caption saved.";
        return RedirectToPage(null, null, $"photo-{id}");
    }

    public async Task<IActionResult> OnPostMoveAsync(int id, int direction, CancellationToken ct)
    {
        var all = await db.GalleryImages.OrderBy(g => g.SortOrder).ThenByDescending(g => g.Id).ToListAsync(ct);
        var index = all.FindIndex(g => g.Id == id);
        if (index < 0) return NotFound();

        var target = index + Math.Sign(direction);
        if (target >= 0 && target < all.Count)
        {
            (all[index], all[target]) = (all[target], all[index]);
            // Renumber everything so the order stays clean, whatever it was before.
            for (var i = 0; i < all.Count; i++) all[i].SortOrder = i * 10;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToPage(null, null, $"photo-{id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var image = await db.GalleryImages.FindAsync([id], ct);
        if (image is null) return NotFound();

        db.GalleryImages.Remove(image);
        await db.SaveChangesAsync(ct);
        photos.Delete(image.FileName, image.ThumbnailFileName);

        TempData["Flash"] = "Photo deleted.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct) =>
        Images = await db.GalleryImages.AsNoTracking().OrderBy(g => g.SortOrder).ThenByDescending(g => g.Id).ToListAsync(ct);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Plural(int n) => n == 1 ? "1 photo" : $"{n} photos";
}

public class CaptionInput
{
    [StringLength(300), Display(Name = "Caption (Swedish, optional)")]
    public string? Caption { get; set; }

    [StringLength(300), Display(Name = "Caption in English (optional)")]
    public string? CaptionEn { get; set; }
}
