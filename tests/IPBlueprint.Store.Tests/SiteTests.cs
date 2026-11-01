using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

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

    [Theory]
    [InlineData("/Cart")]
    [InlineData("/Custom")]
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
}
