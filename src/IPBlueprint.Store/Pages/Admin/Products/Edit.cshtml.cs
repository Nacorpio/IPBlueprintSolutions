using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Pages.Admin.Products;

[RequestSizeLimit(FileStore.MaxProductImageBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FileStore.MaxProductImageBytes + 1024 * 1024)]
public partial class EditModel(StoreDbContext db, FileStore files) : PageModel
{
    [BindProperty]
    public ProductInput Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Image { get; set; }

    public int? Id { get; private set; }
    public string? CurrentImage { get; private set; }
    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is null) return Page();

        var p = await db.Products.FindAsync([id.Value], ct);
        if (p is null) return NotFound();

        Id = p.Id;
        CurrentImage = p.ImageFileName;
        Input = new ProductInput
        {
            Name = p.Name, NameEn = p.NameEn, Slug = p.Slug, Description = p.Description, DescriptionEn = p.DescriptionEn,
            Price = p.Price, SortOrder = p.SortOrder, IsActive = p.IsActive,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, CancellationToken ct)
    {
        Product? product = null;
        if (id is not null)
        {
            product = await db.Products.FindAsync([id.Value], ct);
            if (product is null) return NotFound();
            Id = product.Id;
            CurrentImage = product.ImageFileName;
        }

        var slug = Slugify(string.IsNullOrWhiteSpace(Input.Slug) ? Input.Name : Input.Slug);
        if (slug.Length == 0)
            ModelState.AddModelError("Input.Slug", "Please use some letters or numbers.");
        else if (await db.Products.AnyAsync(p => p.Slug == slug && p.Id != (id ?? 0), ct))
            ModelState.AddModelError("Input.Slug", "Another product already uses this address.");

        if (!ModelState.IsValid) return Page();

        string? newImage = null;
        if (Image is { Length: > 0 })
        {
            var (name, error) = await files.SaveProductImageAsync(Image, ct);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(Image), error);
                return Page();
            }
            newImage = name;
        }

        if (product is null)
        {
            product = new Product { Name = Input.Name.Trim(), Slug = slug };
            db.Products.Add(product);
        }

        product.Name = Input.Name.Trim();
        product.Slug = slug;
        product.Description = Input.Description?.Trim() ?? "";
        product.NameEn = NullIfEmpty(Input.NameEn);
        product.DescriptionEn = NullIfEmpty(Input.DescriptionEn);
        product.Price = decimal.Round(Input.Price, 2);
        product.SortOrder = Input.SortOrder;
        product.IsActive = Input.IsActive;

        var oldImage = product.ImageFileName;
        if (newImage is not null) product.ImageFileName = newImage;

        await db.SaveChangesAsync(ct);
        if (newImage is not null) files.DeleteProductImage(oldImage);

        TempData["Flash"] = $"Saved {product.Name}.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();

        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        files.DeleteProductImage(product.ImageFileName);

        TempData["Flash"] = $"Deleted {product.Name}.";
        return RedirectToPage("Index");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"Drakfigur – Stor!" becomes "drakfigur-stor". Swedish å/ä/ö become a/a/o.</summary>
    public static string Slugify(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        var ascii = NonSlugChars().Replace(sb.ToString().Normalize(NormalizationForm.FormC), "-");
        return ascii.Trim('-') is { Length: > 80 } s ? s[..80].Trim('-') : ascii.Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}

public class ProductInput
{
    [Required, StringLength(120), Display(Name = "Name (Swedish)")]
    public string Name { get; set; } = "";

    [StringLength(120), Display(Name = "Name in English (optional)")]
    public string? NameEn { get; set; }

    [StringLength(80), Display(Name = "Web address")]
    public string? Slug { get; set; }

    [StringLength(4000), Display(Name = "Description (Swedish)")]
    public string? Description { get; set; }

    [StringLength(4000), Display(Name = "Description in English (optional)")]
    public string? DescriptionEn { get; set; }

    [Range(1, 100000), Display(Name = "Price (kr, incl. VAT)")]
    public decimal Price { get; set; }

    [Display(Name = "Sort order")]
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
