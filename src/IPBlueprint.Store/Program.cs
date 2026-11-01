using System.Threading.RateLimiting;
using IPBlueprint.Store.Admin;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Payments;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

services.Configure<ShopOptions>(config.GetSection(ShopOptions.Section));
services.Configure<SumUpOptions>(config.GetSection(SumUpOptions.Section));
services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));

// Storage: database, product photos and customer uploads all live under one data folder.
var dataPath = config.GetSection(StorageOptions.Section).Get<StorageOptions>()?.DataPath ?? "App_Data";
var storage = new StoragePaths(Path.GetFullPath(dataPath, builder.Environment.ContentRootPath));
services.AddSingleton(storage);
services.AddSingleton<FileStore>();

services.AddDbContext<StoreDbContext>(o => o.UseSqlite($"Data Source={storage.DatabaseFile}"));

services.AddSingleton(TimeProvider.System);
services.AddHttpContextAccessor();
services.AddScoped<CartService>();
services.AddScoped<OrderService>();
services.AddScoped<OrderNotifications>();

services.AddHttpClient<ISumUpClient, SumUpClient>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SumUpOptions>>().Value.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
});

if (string.IsNullOrWhiteSpace(config[$"{SmtpOptions.Section}:Host"]))
    services.AddSingleton<IEmailSender, LoggingEmailSender>();
else
    services.AddSingleton<IEmailSender, SmtpEmailSender>();

services.AddAuthentication(AdminPolicy.Scheme)
    .AddCookie(AdminPolicy.Scheme, o =>
    {
        o.LoginPath = "/Admin/Login";
        o.LogoutPath = "/Admin/Logout";
        o.Cookie.Name = "admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
services.AddAuthorizationBuilder()
    .AddPolicy(AdminPolicy.Name, p => p.AddAuthenticationSchemes(AdminPolicy.Scheme).RequireAuthenticatedUser());

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AdminPolicy.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
});

services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", AdminPolicy.Name);
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
});

// Behind a reverse proxy (nginx, Caddy) the real scheme and client IP arrive in forwarded headers.
// Only trust them when told to, otherwise anyone could fake their IP and dodge the login rate limit.
var behindProxy = config.GetValue<bool>("Hosting:BehindReverseProxy");
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (behindProxy)
    {
        // The proxy runs on the same machine or private network and is the only way in.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment()) await DevelopmentSeed.RunAsync(db);
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// Product photos are public; customer attachments are not (admins download them through an authorized page).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storage.ProductImages),
    RequestPath = "/images/products",
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public,max-age=604800",
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

// SumUp calls this when a checkout's status changes. The body is just {"event_type": "...", "id": "<checkout id>"};
// we never trust it on its own and always ask SumUp for the checkout. A non-2xx answer makes SumUp retry later.
app.MapPost("/webhooks/sumup", async (SumUpWebhook? payload, OrderService orders, ILogger<Program> log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(payload?.Id)) return Results.BadRequest();

    try
    {
        var result = await orders.SyncCheckoutAsync(payload.Id, ct);
        log.LogInformation("SumUp webhook {Event} for checkout {CheckoutId}: {Result}", payload.EventType, payload.Id, result);
        return Results.Ok();
    }
    catch (SumUpException ex)
    {
        log.LogWarning(ex, "Could not verify SumUp checkout {CheckoutId}; SumUp will retry", payload.Id);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

internal sealed record SumUpWebhook(
    [property: System.Text.Json.Serialization.JsonPropertyName("event_type")] string? EventType,
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id);

public partial class Program;
