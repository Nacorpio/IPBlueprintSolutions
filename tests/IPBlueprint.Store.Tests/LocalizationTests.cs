using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IPBlueprint.Store.Tests;

public partial class LocalizationTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public LocalizationTests(StoreFactory factory) => _factory = factory;

    private HttpClient EnglishClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{Languages.CookieName}={Uri.EscapeDataString(Languages.CookieValue("en"))}");
        return client;
    }

    [Fact]
    public async Task Site_is_Swedish_by_default_even_for_English_browsers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        var html = await client.GetStringAsync("/Cart");

        Assert.Contains("<html lang=\"sv\">", html);
        Assert.Contains("Din varukorg", html);
        Assert.Contains(">English</a>", html);
    }

    [Fact]
    public async Task Language_cookie_switches_to_English()
    {
        var html = await EnglishClient().GetStringAsync("/Cart");

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("Your cart", html);
        Assert.Contains(">Svenska</a>", html);
    }

    [Fact]
    public async Task Language_switch_sets_a_lasting_cookie_and_returns_to_the_same_page()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/language/en?returnUrl=%2FInfo%2FTerms");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Info/Terms", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(Languages.CookieName + "="));
        Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("//evil.test/")]
    [InlineData("/\\evil.test/")]
    public async Task Language_switch_never_redirects_off_site(string returnUrl)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/language/sv?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Legal_pages_have_both_languages()
    {
        Assert.Contains("Köpvillkor", await _factory.CreateClient().GetStringAsync("/Info/Terms"));
        Assert.Contains("Terms of purchase", await EnglishClient().GetStringAsync("/Info/Terms"));
        Assert.Contains("Integritetspolicy", await _factory.CreateClient().GetStringAsync("/Info/Privacy"));
        Assert.Contains("Retur och ångerrätt", await _factory.CreateClient().GetStringAsync("/Info/Returns"));
    }

    [Fact]
    public async Task English_visitors_see_the_English_product_name_or_the_Swedish_one_if_there_is_none()
    {
        var withEnglish = await _factory.WithDbAsync(async db =>
        {
            var p = new Product { Name = "Ugglelampa", NameEn = "Owl lamp", Slug = "ugglelampa", Price = 199m };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            return p;
        });
        var swedishOnly = await _factory.AddProductAsync("Tandborstställ", 89m);

        var swedish = await _factory.CreateClient().GetStringAsync("/");
        var english = await EnglishClient().GetStringAsync("/");

        Assert.Contains("Ugglelampa", swedish);
        Assert.DoesNotContain("Owl lamp", swedish);
        Assert.Contains("Owl lamp", english);
        Assert.Contains(swedishOnly.Name, english);
    }

    [Fact]
    public async Task Validation_messages_follow_the_language()
    {
        var product = await _factory.AddProductAsync("Testvara", 100m);

        async Task<string> PostEmptyCheckout(HttpClient client)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:1");
            var form = await client.GetStringAsync("/Checkout");
            var token = AntiforgeryToken().Match(form).Groups[1].Value;
            var response = await client.PostAsync("/Checkout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.PostalCode"] = "abc",
            }));
            return await response.Content.ReadAsStringAsync();
        }

        var swedish = await PostEmptyCheckout(_factory.CreateClient());
        var english = await PostEmptyCheckout(EnglishClient());

        Assert.Contains("Fyll i ditt namn.", swedish);
        Assert.Contains("Ange ett svenskt postnummer", swedish);
        Assert.Contains("Du behöver godkänna villkoren", swedish);
        Assert.Contains("Please enter your name.", english);
        Assert.Contains("Enter a Swedish postcode", english);
    }

    [Theory]
    [InlineData("sv", "Tack för din beställning")]
    [InlineData("en", "Thank you for your order")]
    public async Task Order_email_is_in_the_customers_language_and_owner_email_in_Swedish(string language, string expectedSubject)
    {
        var order = new Order
        {
            Reference = "IPB-TEST-" + language.ToUpperInvariant(), CustomerName = "Anna", Email = "anna@example.se",
            AddressLine1 = "Storgatan 1", PostalCode = "123 45", City = "Stockholm", Language = language, Total = 100m,
            Lines = [new OrderLine { ProductName = "Kruka", UnitPrice = 100m, Quantity = 1 }],
        };
        await using var scope = _factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<OrderNotifications>().OrderPaidAsync(order);

        var sent = _factory.Emails.Sent.Where(e => e.Subject.Contains(order.Reference)).ToList();
        Assert.Contains(sent, e => e.To == "anna@example.se" && e.Subject.StartsWith(expectedSubject));
        Assert.Contains(sent, e => e.To == "owner@shop.test" && e.Subject.StartsWith("Ny betald beställning"));
    }

    /// <summary>Every English text used on the customer-facing pages needs a Swedish entry, or Swedish visitors would
    /// see English. Admin pages are left in English on purpose.</summary>
    [Fact]
    public void Every_text_on_the_public_pages_has_a_Swedish_translation()
    {
        var project = Path.Combine(RepoRoot(), "src", "IPBlueprint.Store");
        var swedish = XDocument.Load(Path.Combine(project, "Localization", "SharedResource.sv.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value"));

        var used = Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cshtml") || f.EndsWith(".cs"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Admin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => TextKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Select(k => k.Replace("\\\"", "\""))
            .ToHashSet();

        Assert.NotEmpty(used);
        var missing = used.Where(k => string.IsNullOrWhiteSpace(swedish.GetValueOrDefault(k))).Order().ToList();
        Assert.True(missing.Count == 0, "Missing Swedish text for:\n" + string.Join("\n", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPBlueprintSolutions.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [GeneratedRegex(@"\b(?:L|localizer)\[""(?<key>(?:[^""\\]|\\.)*)""|ErrorMessage = ""(?<key>(?:[^""\\]|\\.)*)""|Display\(Name = ""(?<key>(?:[^""\\]|\\.)*)""")]
    private static partial Regex TextKey();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
