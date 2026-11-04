using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

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
