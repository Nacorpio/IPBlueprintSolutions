using System.Collections.Concurrent;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IPBlueprint.Store.Tests;

/// <summary>Runs the real app against a throwaway SQLite database, with SumUp and email replaced by fakes.</summary>
public sealed class StoreFactory : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "ipb-tests-" + Guid.NewGuid().ToString("N"));

    public FakeSumUp SumUp { get; } = new();
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DataPath", _dataPath);
        builder.UseSetting("SumUp:ApiKey", "sk_test");
        builder.UseSetting("SumUp:MerchantCode", "MTEST");
        builder.UseSetting("Shop:PublicBaseUrl", "https://shop.test");
        builder.UseSetting("Shop:NotificationEmails", "owner@shop.test");
        builder.UseSetting("Admin:Password", "correct horse battery staple");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISumUpClient>();
            services.AddSingleton<ISumUpClient>(SumUp);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    public async Task<T> WithDbAsync<T>(Func<StoreDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<StoreDbContext>());
    }

    public async Task<Product> AddProductAsync(string name, decimal price)
    {
        return await WithDbAsync(async db =>
        {
            var p = new Product { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N")[..6], Price = price };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            return p;
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataPath, recursive: true); } catch (IOException) { }
    }
}

public sealed class FakeSumUp : ISumUpClient
{
    public ConcurrentDictionary<string, SumUpCheckout> Checkouts { get; } = new();
    public List<CreateCheckoutRequest> Created { get; } = [];

    public Task<SumUpCheckout> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default)
    {
        lock (Created) Created.Add(request);
        var id = Guid.NewGuid().ToString();
        var checkout = new SumUpCheckout
        {
            Id = id,
            CheckoutReference = request.CheckoutReference,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = "PENDING",
            HostedCheckoutUrl = $"https://checkout.sumup.test/pay/{id}",
        };
        Checkouts[id] = checkout;
        return Task.FromResult(checkout);
    }

    public Task<SumUpCheckout?> GetCheckoutAsync(string checkoutId, CancellationToken ct = default) =>
        Task.FromResult(Checkouts.GetValueOrDefault(checkoutId));

    public void SetStatus(string checkoutId, string status, decimal? amount = null) =>
        Checkouts[checkoutId] = Checkouts[checkoutId] with
        {
            Status = status,
            Amount = amount ?? Checkouts[checkoutId].Amount,
            TransactionCode = status == "PAID" ? "TX123" : null,
        };
}

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string htmlBody, string? replyTo = null, CancellationToken ct = default)
    {
        Sent.Enqueue((to, subject));
        return Task.CompletedTask;
    }
}
