using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Tests;

public partial class FormAbuseTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public FormAbuseTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Custom_form_confirmation_repeats_nothing_the_visitor_typed()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = await client.GetStringAsync("/Custom");
        var email = $"victim-{Guid.NewGuid():N}@example.se";

        var response = await client.PostAsync("/Custom", new MultipartFormDataContent
        {
            { new StringContent(AntiforgeryToken().Match(form).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent("Your parcel is held"), "Input.Name" },
            { new StringContent(email), "Input.Email" },
            { new StringContent("Pay the customs fee at evil.example to release it."), "Input.Description" },
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var confirmation = Assert.Single(_factory.Emails.Sent, e => e.To == email);
        Assert.DoesNotContain("evil.example", confirmation.Body);
        Assert.DoesNotContain("parcel", confirmation.Body);
        Assert.Contains(_factory.Emails.Sent, e => e.To == "owner@shop.test" && e.Body.Contains("evil.example")); // the owners still see it all
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}

/// <summary>Its own app instance, because the rate limits count every post the test server sees.</summary>
public class FormRateLimitTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public FormRateLimitTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Custom_form_posts_are_rate_limited_but_viewing_it_is_not()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await client.PostAsync("/Custom", new FormUrlEncodedContent([]))).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses[..^2]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Custom")).StatusCode);

        var page = await (await client.PostAsync("/Custom", new FormUrlEncodedContent([]))).Content.ReadAsStringAsync();
        Assert.Contains("För många försök", page);
    }
}

/// <summary>Its own app instance, because the rate limits count every post the test server sees.</summary>
public partial class StockAlertRateLimitTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public StockAlertRateLimitTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Back_in_stock_sign_ups_are_rate_limited_but_adding_to_the_cart_is_not()
    {
        var product = await _factory.WithDbAsync(async db =>
        {
            var p = new IPBlueprint.Store.Data.Product { Name = "Slut", Slug = "slut-" + Guid.NewGuid().ToString("N")[..6], Price = 50m, Stock = 0 };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            return p;
        });
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = AntiforgeryToken().Match(await client.GetStringAsync($"/products/{product.Slug}")).Groups[1].Value;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsync($"/products/{product.Slug}?handler=Notify", new FormUrlEncodedContent(
                [new("__RequestVerificationToken", token), new("email", $"stranger{i}@example.se")]));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses[..^1]);
        Assert.Equal(5, await _factory.WithDbAsync(db => db.StockAlerts.CountAsync(a => a.ProductId == product.Id)));

        // Adding to the cart on the same page still works.
        var add = await client.PostAsync($"/products/{product.Slug}", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("quantity", "1")]));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, add.StatusCode);
    }

    [Theory]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.5", "::ffff:203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    public void Rate_limits_count_an_IPv6_slash_64_as_one_visitor(string a, string b, bool same)
    {
        var keyA = IPBlueprint.Store.Shop.FormRateLimits.VisitorKey(System.Net.IPAddress.Parse(a));
        var keyB = IPBlueprint.Store.Shop.FormRateLimits.VisitorKey(System.Net.IPAddress.Parse(b));
        Assert.Equal(same, keyA == keyB);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
