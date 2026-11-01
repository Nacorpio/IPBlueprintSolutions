using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IPBlueprint.Store.Tests;

public partial class SeoTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public SeoTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Robots_txt_points_to_the_sitemap_and_keeps_crawlers_out_of_admin()
    {
        var robots = await _factory.CreateClient().GetStringAsync("/robots.txt");

        Assert.Contains("Sitemap: https://shop.test/sitemap.xml", robots);
        Assert.Contains("Disallow: /admin", robots);
        Assert.DoesNotContain("Disallow: /\n", robots);
    }

    [Fact]
    public async Task Sitemap_lists_pages_and_products_in_both_languages()
    {
        var product = await _factory.AddProductAsync("Sitemap vase", 199m);
        var hidden = await _factory.AddProductAsync("Hidden thing", 99m);
        await _factory.WithDbAsync(async db => { db.Products.Find(hidden.Id)!.IsActive = false; return await db.SaveChangesAsync(); });

        var response = await _factory.CreateClient().GetAsync("/sitemap.xml");
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locs = xml.Descendants(ns + "loc").Select(l => l.Value).ToList();
        Assert.Contains("https://shop.test/", locs);
        Assert.Contains("https://shop.test/?lang=en", locs);
        Assert.Contains("https://shop.test/gallery", locs);
        Assert.Contains($"https://shop.test/products/{product.Slug}", locs);
        Assert.Contains($"https://shop.test/products/{product.Slug}?lang=en", locs);
        Assert.DoesNotContain(locs, l => l.Contains(hidden.Slug));
        Assert.DoesNotContain(locs, l => l.Contains("/admin") || l.Contains("/cart"));

        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var first = xml.Descendants(ns + "url").First();
        Assert.Equal(["sv", "en", "x-default"], first.Elements(xhtml + "link").Select(l => (string)l.Attribute("hreflang")!));
    }

    [Fact]
    public async Task Product_page_has_canonical_hreflang_preview_and_price_data()
    {
        var product = await _factory.AddProductAsync("Preview planter", 349m);

        var html = await _factory.CreateClient().GetStringAsync($"/products/{product.Slug}");
        var url = $"https://shop.test/products/{product.Slug}";

        Assert.Contains($"<link rel=\"canonical\" href=\"{url}\" />", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"sv\" href=\"{url}\" />", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"en\" href=\"{url}?lang=en\" />", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"x-default\" href=\"{url}\" />", html);
        Assert.Contains("<meta property=\"og:type\" content=\"product\" />", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Preview planter\" />", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://shop.test/img/share.png\" />", html);
        Assert.Contains("<meta property=\"product:price:amount\" content=\"349.00\" />", html);
        Assert.DoesNotContain("noindex", html);

        var jsonLd = JsonLd().Match(html).Groups[1].Value;
        Assert.Contains("\"@type\":\"Product\"", jsonLd);
        Assert.Contains("\"price\":\"349.00\"", jsonLd);
        Assert.Contains("\"priceCurrency\":\"SEK\"", jsonLd);
        Assert.Contains($"\"url\":\"{url}\"", jsonLd);
    }

    [Fact]
    public async Task Structured_data_cannot_break_out_of_its_script_tag()
    {
        var product = await _factory.AddProductAsync("Evil", 10m);
        await _factory.WithDbAsync(async db =>
        {
            db.Products.Find(product.Id)!.Description = "</script><script>alert(1)</script>";
            return await db.SaveChangesAsync();
        });

        var html = await _factory.CreateClient().GetStringAsync($"/products/{product.Slug}");

        Assert.DoesNotContain("<script>alert(1)", html);
    }

    [Fact]
    public async Task English_address_shows_English_and_remembers_it()
    {
        var response = await _factory.CreateClient().GetAsync("/gallery?lang=en");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://shop.test/gallery?lang=en\" />", html);
        Assert.Contains("<meta property=\"og:locale\" content=\"en_GB\" />", html);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(Languages.CookieName + "=") && c.Contains("en-GB"));
    }

    [Fact]
    public async Task Address_language_wins_over_the_cookie()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{Languages.CookieName}={Uri.EscapeDataString(Languages.CookieValue("en"))}");

        var html = await client.GetStringAsync("/gallery?lang=sv");

        Assert.Contains("<html lang=\"sv\">", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://shop.test/gallery\" />", html);
    }

    [Fact]
    public async Task Language_switch_is_a_plain_link_to_the_other_language()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Info/About");

        Assert.Contains("href=\"/Info/About?lang=en\"", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://shop.test/info/about\" />", html);
    }

    [Fact]
    public async Task Front_page_describes_the_store()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.Contains("<title>IP Blueprint Solutions · 3D-utskrift i Smedjebacken</title>", html);
        Assert.Contains("<meta name=\"description\"", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://shop.test/\" />", html);
        Assert.Contains("\"@type\":[\"OnlineStore\",\"LocalBusiness\"]", html);
        Assert.Contains("\"sameAs\":[\"https://www.instagram.com/ip.blueprint.solutions/\"", html);
    }

    [Theory]
    [InlineData("/Cart")]
    [InlineData("/Checkout")]
    [InlineData("/Admin/Login")]
    [InlineData("/no-such-page")]
    public async Task Private_and_error_pages_are_kept_out_of_search_results(string url)
    {
        var html = await _factory.CreateClient().GetStringAsync(url.Replace("/no-such-page", "/Error?code=404"));

        Assert.Contains("<meta name=\"robots\" content=\"noindex\" />", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
    }

    [Fact]
    public async Task Footer_links_to_the_shops_social_accounts()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Info/About");

        Assert.Contains("href=\"https://www.instagram.com/ip.blueprint.solutions/\"", html);
        Assert.Contains("href=\"https://www.facebook.com/profile.php?id=61576443942897\"", html);
        Assert.Contains("Se mer av det vi printar på", html);
    }

    [Fact]
    public async Task Social_links_disappear_when_not_set()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Shop:InstagramUrl", "");
            b.UseSetting("Shop:FacebookUrl", "");
        });

        var html = await factory.CreateClient().GetStringAsync("/gallery");

        Assert.DoesNotContain("instagram.com", html);
        Assert.DoesNotContain("class=\"social-icons\"", html);
        Assert.DoesNotContain("follow-us", html);
    }

    [Fact]
    public async Task Order_confirmation_invites_the_customer_to_follow_the_shop()
    {
        var order = new Order
        {
            Reference = "IPB-FOLLOW", CustomerName = "Anna", Email = "follow@example.se",
            AddressLine1 = "Storgatan 1", PostalCode = "123 45", City = "Stockholm", Total = 100m,
            Lines = [new OrderLine { ProductName = "Kruka", UnitPrice = 100m, Quantity = 1 }],
        };
        await using var scope = _factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<OrderNotifications>().OrderPaidAsync(order);

        var email = Assert.Single(_factory.Emails.Sent, e => e.To == "follow@example.se");
        Assert.Contains("Följ oss på <a href=\"https://www.instagram.com/ip.blueprint.solutions/\">Instagram</a> och <a", email.Body);
    }

    [Fact]
    public async Task Shipping_email_invites_the_customer_to_follow_the_shop()
    {
        var order = new Order
        {
            Reference = "IPB-SHIPPED", CustomerName = "Sam", Email = "shipped@example.se", Language = "en",
            AddressLine1 = "Storgatan 1", PostalCode = "123 45", City = "Stockholm", Total = 100m,
        };
        await using var scope = _factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<OrderNotifications>().OrderShippedAsync(order);

        var email = Assert.Single(_factory.Emails.Sent, e => e.To == "shipped@example.se");
        Assert.Contains("Follow us on <a href=\"https://www.instagram.com/ip.blueprint.solutions/\">Instagram</a> and <a", email.Body);
    }

    [Fact]
    public async Task Analytics_are_off_unless_configured()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.DoesNotContain("plausible", html);
        Assert.DoesNotContain("umami", html);
        Assert.DoesNotContain("Besöksstatistik", await _factory.CreateClient().GetStringAsync("/Info/Privacy"));
    }

    [Fact]
    public async Task Umami_loads_on_public_pages_but_not_admin_and_is_in_the_privacy_policy()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Analytics:Provider", "Umami");
            b.UseSetting("Analytics:WebsiteId", "abc-123");
        });
        var client = factory.CreateClient();

        Assert.Contains("<script defer src=\"https://cloud.umami.is/script.js\" data-website-id=\"abc-123\"></script>", await client.GetStringAsync("/"));
        Assert.DoesNotContain("umami", await client.GetStringAsync("/Admin/Login"));
        Assert.Contains("Besöksstatistik", await client.GetStringAsync("/Info/Privacy"));
    }

    [Fact]
    public async Task Plausible_uses_the_sites_own_script()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Analytics:Provider", "plausible");
            b.UseSetting("Analytics:ScriptUrl", "https://plausible.io/js/pa-TEST.js");
        });

        var html = await factory.CreateClient().GetStringAsync("/gallery");

        Assert.Contains("<script async src=\"https://plausible.io/js/pa-TEST.js\"></script>", html);
        Assert.Contains("plausible.init()", html);
    }

    [Theory]
    [InlineData("Plausible", "http://plausible.io/js/x.js", "")]
    [InlineData("Plausible", "", "")]
    [InlineData("Umami", "", "")]
    [InlineData("Matomo", "https://example.test/x.js", "1")]
    public void Incomplete_analytics_settings_stay_off(string provider, string scriptUrl, string websiteId) =>
        Assert.False(new AnalyticsOptions { Provider = provider, ScriptUrl = scriptUrl, WebsiteId = websiteId }.Enabled);

    [Theory]
    [InlineData("Short text.", "Short text.")]
    [InlineData("  spread\n out  ", "spread out")]
    public void Descriptions_are_tidied(string input, string expected) => Assert.Equal(expected, Seo.Describe(input));

    [Fact]
    public void Long_descriptions_are_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60));
        var described = Seo.Describe(text);

        Assert.True(described.Length <= Seo.DescriptionLength);
        Assert.EndsWith("word…", described);
    }

    [GeneratedRegex("<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex JsonLd();
}
