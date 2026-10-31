using System.ComponentModel.DataAnnotations;
using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ProductEdit = IPBlueprint.Store.Pages.Admin.Products.EditModel;

namespace IPBlueprint.Store.Pages.Admin.Categories;

public class EditModel(StoreDbContext db) : PageModel
{
    [BindProperty]
    public CategoryInput Input { get; set; } = new();

    public int? Id { get; private set; }
    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is null) return Page();

        var c = await db.Categories.FindAsync([id.Value], ct);
        if (c is null) return NotFound();

        Id = c.Id;
        Input = new CategoryInput
        {
            Name = c.Name, NameEn = c.NameEn, Slug = c.Slug, Description = c.Description, DescriptionEn = c.DescriptionEn, SortOrder = c.SortOrder,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, CancellationToken ct)
    {
        Category? category = null;
        if (id is not null)
        {
            category = await db.Categories.FindAsync([id.Value], ct);
            if (category is null) return NotFound();
            Id = category.Id;
        }

        var slug = ProductEdit.Slugify(string.IsNullOrWhiteSpace(Input.Slug) ? Input.Name : Input.Slug);
        if (slug.Length == 0)
            ModelState.AddModelError("Input.Slug", "Please use some letters or numbers.");
        else if (await db.Categories.AnyAsync(c => c.Slug == slug && c.Id != (id ?? 0), ct))
            ModelState.AddModelError("Input.Slug", "Another category already uses this address.");

        if (!ModelState.IsValid) return Page();

        if (category is null)
        {
            category = new Category { Name = Input.Name.Trim(), Slug = slug };
            db.Categories.Add(category);
        }

        category.Name = Input.Name.Trim();
        category.NameEn = NullIfEmpty(Input.NameEn);
        category.Slug = slug;
        category.Description = NullIfEmpty(Input.Description);
        category.DescriptionEn = NullIfEmpty(Input.DescriptionEn);
        category.SortOrder = Input.SortOrder;
        await db.SaveChangesAsync(ct);

        TempData["Flash"] = $"Saved {category.Name}.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var category = await db.Categories.FindAsync([id], ct);
        if (category is null) return NotFound();

        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        TempData["Flash"] = $"Deleted {category.Name}.";
        return RedirectToPage("Index");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class CategoryInput
{
    [Required, StringLength(80), Display(Name = "Name (Swedish)")]
    public string Name { get; set; } = "";

    [StringLength(80), Display(Name = "Name in English (optional)")]
    public string? NameEn { get; set; }

    [StringLength(80), Display(Name = "Web address")]
    public string? Slug { get; set; }

    [StringLength(1000), Display(Name = "Description (Swedish, optional)")]
    public string? Description { get; set; }

    [StringLength(1000), Display(Name = "Description in English (optional)")]
    public string? DescriptionEn { get; set; }

    [Display(Name = "Sort order")]
    public int SortOrder { get; set; }
}
