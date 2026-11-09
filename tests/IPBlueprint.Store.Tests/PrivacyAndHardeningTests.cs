using System.Net;
using System.Text.RegularExpressions;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPBlueprint.Store.Tests;

public partial class PrivacyAndHardeningTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public PrivacyAndHardeningTests(StoreFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/")]
    [InlineData("/Admin/Login")]
    [InlineData("/robots.txt")]
    public async Task Every_response_carries_the_browser_protections(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Old_unpaid_orders_and_old_unanswered_requests_are_deleted_but_paid_orders_are_kept()
    {
        var old = DateTime.UtcNow.AddDays(-31);
        var ids = await _factory.WithDbAsync(async db =>
        {
            Order NewOrder(OrderStatus status, DateTime created, string? extra = null) => new()
            {
                Reference = "IPB-TEST-" + Guid.NewGuid().ToString("N")[..8],
                CustomerName = "Anna", Email = "anna@example.se", Status = status, CreatedAtUtc = created,
                ExtraPaymentCheckoutId = extra,
            };
            var oldUnpaid = NewOrder(OrderStatus.AwaitingPayment, old);
            var oldCancelled = NewOrder(OrderStatus.Cancelled, old);
            var oldPaid = NewOrder(OrderStatus.Delivered, old);
            var newUnpaid = NewOrder(OrderStatus.PaymentFailed, DateTime.UtcNow.AddDays(-2));
            var oldWithStrayPayment = NewOrder(OrderStatus.AwaitingPayment, old, extra: "chk-wrong-amount");
            CustomOrderRequest NewRequest(CustomRequestStatus status, DateTime created) => new()
            {
                Name = "Kim", Email = "kim@example.se", Description = "A dragon", Status = status, CreatedAtUtc = created,
            };
            var oldOpenRequest = NewRequest(CustomRequestStatus.Quoted, DateTime.UtcNow.AddDays(-400));
            var oldAcceptedRequest = NewRequest(CustomRequestStatus.Accepted, DateTime.UtcNow.AddDays(-400));
            var newRequest = NewRequest(CustomRequestStatus.New, DateTime.UtcNow.AddDays(-30));
            db.AddRange(oldUnpaid, oldCancelled, oldPaid, newUnpaid, oldWithStrayPayment, oldOpenRequest, oldAcceptedRequest, newRequest);
            await db.SaveChangesAsync();
            return new
            {
                Deleted = new[] { oldUnpaid.Id, oldCancelled.Id },
                Kept = new[] { oldPaid.Id, newUnpaid.Id, oldWithStrayPayment.Id },
                DeletedRequest = oldOpenRequest.Id,
                KeptRequests = new[] { oldAcceptedRequest.Id, newRequest.Id },
            };
        });

        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DataRetention>().CleanUpAsync();

        var orders = await _factory.WithDbAsync(db => db.Orders.Select(o => o.Id).ToListAsync());
        Assert.DoesNotContain(orders, ids.Deleted.Contains);
        Assert.All(ids.Kept, id => Assert.Contains(id, orders));
        var requests = await _factory.WithDbAsync(db => db.CustomOrderRequests.Select(r => r.Id).ToListAsync());
        Assert.DoesNotContain(ids.DeletedRequest, requests);
        Assert.All(ids.KeptRequests, id => Assert.Contains(id, requests));
    }

    [Fact]
    public async Task Admin_can_delete_a_custom_request()
    {
        var id = await _factory.WithDbAsync(async db =>
        {
            var r = new CustomOrderRequest { Name = "Forget me", Email = "forget@example.se", Description = "Please delete" };
            db.CustomOrderRequests.Add(r);
            await db.SaveChangesAsync();
            return r.Id;
        });
        var client = await AdminClientAsync();
        var token = Token(await client.GetStringAsync($"/Admin/Requests/Details/{id}"));

        var response = await client.PostAsync($"/Admin/Requests/Details/{id}?handler=Delete", Form(token));

        response.EnsureSuccessStatusCode();
        Assert.False(await _factory.WithDbAsync(db => db.CustomOrderRequests.AnyAsync(r => r.Id == id)));
    }

    [Fact]
    public async Task Without_SMTP_a_live_shop_logs_only_the_subject_of_an_email()
    {
        var logs = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(new FakeEnvironment(Environments.Production), logs);

        await sender.SendAsync("anna@example.se", "Tack för din beställning", "<a href=\"https://shop.test/order/secret\">order</a>");

        var line = Assert.Single(logs.Lines);
        Assert.Contains("Tack för din beställning", line);
        Assert.DoesNotContain("anna@example.se", line);
        Assert.DoesNotContain("secret", line);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var token = Token(await client.GetStringAsync("/Admin/Login"));
        (await client.PostAsync("/Admin/Login", Form(token, ("password", "correct horse battery staple")))).EnsureSuccessStatusCode();
        return client;
    }

    internal static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);

    internal static string Token(string html) => AntiforgeryToken().Match(html).Groups[1].Value;

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "IPBlueprint.Store";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}

/// <summary>Its own app instance, because it changes the admin password.</summary>
public class AdminPasswordChangeTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public AdminPasswordChangeTests(StoreFactory factory) => _factory = factory;

    [Fact]
    public async Task Changing_the_admin_password_logs_out_existing_sessions()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = PrivacyAndHardeningTests.Token(await client.GetStringAsync("/Admin/Login"));
        await client.PostAsync("/Admin/Login", PrivacyAndHardeningTests.Form(token, ("password", "correct horse battery staple")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);

        var config = (IConfigurationRoot)_factory.Services.GetRequiredService<IConfiguration>();
        config["Admin:Password"] = "a brand new password 2026";
        config.Reload();

        var response = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Admin/Login", response.Headers.Location!.PathAndQuery);
    }
}
