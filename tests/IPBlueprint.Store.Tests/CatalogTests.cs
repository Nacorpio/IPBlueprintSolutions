using System.Net;
using IPBlueprint.Store.Data;

namespace IPBlueprint.Store.Tests;

public class CatalogTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public CatalogTests(StoreFactory factory) => _factory = factory;

    private async Task<(Category Planters, Category Figures)> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return await _factory.WithDbAsync(async db =>
        {
            var planters = new Category { Slug = "krukor-" + suffix, Name = "Krukor " + suffix, NameEn = "Planters " + suffix };
            var figures = new Category { Slug = "figurer-" + suffix, Name = "Figurer " + suffix };
            db.Categories.AddRange(planters, figures);
            db.Products.AddRange(
                new Product { Slug = "billig-" + suffix, Name = "Billig kruka " + suffix, Price = 99m, Material = PrintMaterial.PLA, Colour = "Svart", ColourEn = "Black", Categories = [planters] },
                new Product { Slug = "dyr-" + suffix, Name = "Dyr kruka " + suffix, Price = 499m, Material = PrintMaterial.PETG, Colour = "Vit", Categories = [planters] },
                new Product { Slug = "drake-" + suffix, Name = "Drake " + suffix, Price = 299m, Material = PrintMaterial.PLA, Categories = [figures] },
                new Product { Slug = "dold-" + suffix, Name = "Dold kruka " + suffix, Price = 10m, IsActive = false, Categories = [planters] });
            await db.SaveChangesAsync();
            return (planters, figures);
        });
    }

    [Fact]
    public async Task Category_page_lists_only_its_own_visible_products()
    {
        var (planters, _) = await SeedAsync();
        var suffix = planters.Slug[^6..];

        var html = await _factory.CreateClient().GetStringAsync($"/categories/{planters.Slug}");

        Assert.Contains($"<h1>{planters.Name}</h1>", html);
        Assert.Contains("Billig kruka " + suffix, html);
        Assert.Contains("Dyr kruka " + suffix, html);
        Assert.DoesNotContain("Drake " + suffix, html);
        Assert.DoesNotContain("Dold kruka " + suffix, html);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://shop.test/categories/{planters.Slug}\"", html);
    }

    [Fact]
    public async Task Category_name_follows_the_language()
    {
        var (planters, figures) = await SeedAsync();

        var html = await _factory.CreateClient().GetStringAsync($"/categories/{planters.Slug}?lang=en");

        Assert.Contains($"<h1>{planters.NameEn}</h1>", html);
        Assert.Contains(figures.Name, html); // no English name, so the Swedish one in the tabs
    }

    [Fact]
    public async Task Unknown_category_is_404()
    {
        var response = await _factory.CreateClient().GetAsync("/categories/finns-inte");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Material_and_colour_filters_narrow_the_list()
    {
        var (planters, _) = await SeedAsync();
        var suffix = planters.Slug[^6..];
        var client = _factory.CreateClient();

        var petg = await client.GetStringAsync($"/categories/{planters.Slug}?material=petg");
        Assert.Contains("Dyr kruka " + suffix, petg);
        Assert.DoesNotContain("Billig kruka " + suffix, petg);

        var black = await client.GetStringAsync($"/?colour=svart");
        Assert.Contains("Billig kruka " + suffix, black);
        Assert.DoesNotContain("Dyr kruka " + suffix, black);
        Assert.DoesNotContain("Drake " + suffix, black);

        var englishLabel = await client.GetStringAsync($"/categories/{planters.Slug}?lang=en");
        Assert.Contains("<option value=\"svart\">Black</option>", englishLabel);
    }

    [Fact]
    public async Task Price_sort_orders_the_products()
    {
        var (planters, _) = await SeedAsync();
        var suffix = planters.Slug[^6..];

        var up = await _factory.CreateClient().GetStringAsync($"/categories/{planters.Slug}?sort=price-asc");
        var down = await _factory.CreateClient().GetStringAsync($"/categories/{planters.Slug}?sort=price-desc");

        Assert.True(up.IndexOf("Billig kruka " + suffix) < up.IndexOf("Dyr kruka " + suffix));
        Assert.True(down.IndexOf("Dyr kruka " + suffix) < down.IndexOf("Billig kruka " + suffix));
    }

    [Fact]
    public async Task Shop_has_category_tabs_and_sitemap_lists_category_pages()
    {
        var (planters, figures) = await SeedAsync();
        var empty = await _factory.WithDbAsync(async db =>
        {
            var c = new Category { Slug = "tom-" + Guid.NewGuid().ToString("N")[..6], Name = "Tom" };
            db.Categories.Add(c);
            await db.SaveChangesAsync();
            return c;
        });

        var shop = await _factory.CreateClient().GetStringAsync("/");
        var sitemap = await _factory.CreateClient().GetStringAsync("/sitemap.xml");

        Assert.Contains($"href=\"/categories/{planters.Slug}\"", shop);
        Assert.DoesNotContain($"/categories/{empty.Slug}", shop);
        Assert.Contains($"<loc>https://shop.test/categories/{figures.Slug}</loc>", sitemap);
        Assert.Contains($"<loc>https://shop.test/categories/{figures.Slug}?lang=en</loc>", sitemap);
        Assert.DoesNotContain(empty.Slug, sitemap);
    }

    [Fact]
    public async Task Product_structured_data_reports_availability()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await _factory.WithDbAsync(async db =>
        {
            db.Products.AddRange(
                new Product { Slug = "slut-" + suffix, Name = "Slut", Price = 1m, Stock = 0 },
                new Product { Slug = "lager-" + suffix, Name = "Lager", Price = 1m, Stock = 5 },
                new Product { Slug = "bestall-" + suffix, Name = "Beställ", Price = 1m });
            return await db.SaveChangesAsync();
        });
        var client = _factory.CreateClient();

        Assert.Contains("schema.org/OutOfStock", await client.GetStringAsync($"/products/slut-{suffix}"));
        Assert.Contains("schema.org/InStock", await client.GetStringAsync($"/products/lager-{suffix}"));
        Assert.Contains("schema.org/MadeToOrder", await client.GetStringAsync($"/products/bestall-{suffix}"));
    }
}
