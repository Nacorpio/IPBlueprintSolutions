using IPBlueprint.Store.Data;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Shop;

/// <summary>A colour in the filter. The key is the Swedish name in lower case, so links work in both languages.</summary>
public sealed record ColourOption(string Key, string Label);

/// <summary>What the shop page shows: the products after filtering, plus what can be filtered on.</summary>
public sealed record CatalogView
{
    public required IReadOnlyList<Product> Products { get; init; }

    /// <summary>Categories that have something in them, for the tabs.</summary>
    public required IReadOnlyList<Category> Categories { get; init; }

    /// <summary>The category page being shown, or null for the whole shop.</summary>
    public Category? Category { get; init; }

    /// <summary>Materials and colours that occur among the products in this category (before the other filters).</summary>
    public required IReadOnlyList<PrintMaterial> Materials { get; init; }
    public required IReadOnlyList<ColourOption> Colours { get; init; }

    public PrintMaterial? Material { get; init; }
    public string? Colour { get; init; }
    public string Sort { get; init; } = Catalog.SortFeatured;

    /// <summary>How many products there are before material and colour filters.</summary>
    public int InScope { get; init; }

    public bool IsFiltered => Material is not null || Colour is not null;
}

/// <summary>
/// Browsing the shop: category, material and colour filters and sorting. The shop is small (tens of products), so
/// everything after "active products in this category" happens in memory. That also sidesteps SQLite's lack of
/// decimal ordering.
/// </summary>
public sealed class Catalog(StoreDbContext db)
{
    public const string SortFeatured = "";
    public const string SortNewest = "newest";
    public const string SortPriceLow = "price-asc";
    public const string SortPriceHigh = "price-desc";

    public static readonly IReadOnlyList<string> Sorts = [SortFeatured, SortNewest, SortPriceLow, SortPriceHigh];

    /// <summary>Returns null when <paramref name="categorySlug"/> names no category.</summary>
    public async Task<CatalogView?> BrowseAsync(string? categorySlug, string? material, string? colour, string? sort, CancellationToken ct = default)
    {
        var products = await db.Products.AsNoTracking()
            .Include(p => p.Categories)
            .Where(p => p.IsActive)
            .ToListAsync(ct);

        var categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        // Tabs only for categories with something to show (plus the one being looked at, so it never vanishes).
        var inUse = products.SelectMany(p => p.Categories).Select(c => c.Id).ToHashSet();

        Category? category = null;
        if (categorySlug is not null)
        {
            category = categories.FirstOrDefault(c => c.Slug == categorySlug.ToLowerInvariant());
            if (category is null) return null;
            products = products.Where(p => p.Categories.Any(c => c.Id == category.Id)).ToList();
        }
        var usedCategories = categories.Where(c => inUse.Contains(c.Id) || c.Id == category?.Id).ToList();

        var materials = products.Where(p => p.Material is not null).Select(p => p.Material!.Value).Distinct().Order().ToList();
        var colours = products
            .Where(p => !string.IsNullOrWhiteSpace(p.Colour))
            .GroupBy(p => ColourKey(p.Colour!))
            .Select(g => new ColourOption(g.Key, g.First().DisplayColour!.Trim()))
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PrintMaterial? materialFilter = Enum.TryParse<PrintMaterial>(material, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : null;
        var colourFilter = string.IsNullOrWhiteSpace(colour) ? null : ColourKey(colour);
        var sortKey = Sorts.Contains(sort ?? "") ? sort ?? "" : SortFeatured;

        var inScope = products.Count;
        IEnumerable<Product> shown = products;
        if (materialFilter is { } mf) shown = shown.Where(p => p.Material == mf);
        if (colourFilter is not null) shown = shown.Where(p => p.Colour is not null && ColourKey(p.Colour) == colourFilter);

        shown = sortKey switch
        {
            SortNewest => shown.OrderByDescending(p => p.CreatedAtUtc).ThenBy(p => p.Name),
            SortPriceLow => shown.OrderBy(p => p.Price).ThenBy(p => p.Name),
            SortPriceHigh => shown.OrderByDescending(p => p.Price).ThenBy(p => p.Name),
            // Things you can buy first; sold-out products stay visible (so people can ask to be told) but last.
            _ => shown.OrderBy(p => p.IsSoldOut).ThenBy(p => p.SortOrder).ThenBy(p => p.Name),
        };

        return new CatalogView
        {
            Products = shown.ToList(),
            Categories = usedCategories,
            Category = category,
            Materials = materials,
            Colours = colours,
            Material = materialFilter,
            Colour = colourFilter,
            Sort = sortKey,
            InScope = inScope,
        };
    }

    public static string ColourKey(string colour) => colour.Trim().ToLowerInvariant();
}
