using System.Net;
using IPBlueprint.Store.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Tests;

public class SiteTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public SiteTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Shop_lists_active_products()
    {
        await _factory.AddProductAsync("Articulated dragon", 349m);

        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.Contains("Articulated dragon", html);
        Assert.Contains("349 kr", html);
    }

    private async Task<Product> AddStockedProductAsync(string name, decimal price, int stock)
    {
        var product = await _factory.AddProductAsync(name, price);
        await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, stock)));
        return product;
    }

    [Fact]
    public async Task Header_cart_preview_lists_what_is_in_the_cart_without_changing_it()
    {
        var product = await AddStockedProductAsync("Liten vas", price: 120m, stock: 3);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:5");

        var response = await client.GetAsync("/gallery");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("class=\"cart-preview\"", html);
        Assert.Contains("Liten vas", html);
        Assert.Contains("3 × 120 kr", html); // capped at what's in stock, like the cart page
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("cart=")));
    }

    [Fact]
    public async Task Cart_page_has_no_header_preview()
    {
        var product = await AddStockedProductAsync("Skål", price: 100m, stock: 3);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:1");

        Assert.DoesNotContain("class=\"cart-preview\"", await client.GetStringAsync("/Cart"));
    }

    [Fact]
    public async Task Header_has_no_cart_preview_when_the_cart_is_empty()
    {
        Assert.DoesNotContain("class=\"cart-preview\"", await _factory.CreateClient().GetStringAsync("/gallery"));
    }

    [Theory]
    [InlineData("/Cart")]
    [InlineData("/Custom")]
    [InlineData("/gallery")]
    [InlineData("/Info/Terms")]
    [InlineData("/Info/Returns")]
    [InlineData("/Info/Privacy")]
    [InlineData("/Info/About")]
    [InlineData("/Admin/Login")]
    public async Task Public_pages_load(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Admin")]
    [InlineData("/Admin/Orders")]
    [InlineData("/Admin/Products/Edit")]
    [InlineData("/Admin/Requests")]
    [InlineData("/Admin/Gallery")]
    public async Task Admin_pages_require_login(string url)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Admin/Login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Unknown_page_returns_404()
    {
        var response = await _factory.CreateClient().GetAsync("/products/no-such-thing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Gallery")]
    [InlineData("/Custom")]
    [InlineData("/Info/About")]
    public async Task Customer_pages_show_the_full_blueprint_background(string url)
    {
        var html = await _factory.CreateClient().GetStringAsync(url);

        Assert.Contains("<div class=\"blueprint\" aria-hidden=\"true\">", html);
        Assert.Contains("bp-near", html);
        Assert.Contains("bp-title", html);
    }

    [Theory]
    [InlineData("/Cart")]
    [InlineData("/Checkout")]
    [InlineData("/Info/Terms")]
    [InlineData("/Info/Returns")]
    [InlineData("/Info/Privacy")]
    public async Task Form_and_legal_pages_get_a_calmer_blueprint_without_near_layer_or_title_block(string url)
    {
        var html = await _factory.CreateClient().GetStringAsync(url);

        Assert.Contains("<div class=\"blueprint is-calm\" aria-hidden=\"true\">", html);
        Assert.DoesNotContain("bp-near", html);
        Assert.DoesNotContain("bp-title", html);
    }

    [Fact]
    public async Task Admin_pages_have_no_blueprint_background()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Admin/Login");

        Assert.DoesNotContain("class=\"blueprint", html);
    }
}
