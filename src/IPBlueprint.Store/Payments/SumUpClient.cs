using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Payments;

/// <summary>Bound from the "SumUp" configuration section. Keep the API key out of source control
/// (use user-secrets locally and an environment variable such as SumUp__ApiKey on the server).</summary>
public class SumUpOptions
{
    public const string Section = "SumUp";

    public string BaseUrl { get; set; } = "https://api.sumup.com/";

    /// <summary>Secret API key from the SumUp dashboard (starts with "sk_").</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Your SumUp merchant code, e.g. "MH4H92C7".</summary>
    public string MerchantCode { get; set; } = "";

    public string Currency { get; set; } = "SEK";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(MerchantCode);
}

public sealed record CreateCheckoutRequest(
    string CheckoutReference,
    decimal Amount,
    string Currency,
    string Description,
    Uri RedirectUrl,
    Uri WebhookUrl);

/// <summary>The parts of a SumUp checkout this shop uses. Status is "PENDING", "PAID", "FAILED" or "EXPIRED".</summary>
public sealed record SumUpCheckout
{
    public string Id { get; init; } = "";
    public string CheckoutReference { get; init; } = "";
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public string Status { get; init; } = "";
    public string? HostedCheckoutUrl { get; init; }
    public string? TransactionCode { get; init; }
    public List<SumUpTransaction>? Transactions { get; init; }

    public bool IsPaid => string.Equals(Status, "PAID", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => Status is "FAILED" or "EXPIRED";
}

public sealed record SumUpTransaction
{
    public string? Id { get; init; }
    public string? TransactionCode { get; init; }
    public string? Status { get; init; }
}

public interface ISumUpClient
{
    Task<SumUpCheckout> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default);

    /// <summary>Returns null when SumUp has no checkout with that id.</summary>
    Task<SumUpCheckout?> GetCheckoutAsync(string checkoutId, CancellationToken ct = default);
}

/// <summary>
/// Calls the SumUp Checkouts API (https://developer.sumup.com/api/checkouts) and uses SumUp's Hosted Checkout,
/// so card details never touch this site.
/// </summary>
public sealed class SumUpClient(HttpClient http, IOptions<SumUpOptions> options, ILogger<SumUpClient> logger) : ISumUpClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<SumUpCheckout> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        EnsureConfigured(o);

        var body = new
        {
            checkout_reference = request.CheckoutReference,
            amount = decimal.Round(request.Amount, 2), // SumUp takes major units: 349.00 means 349 kr
            currency = request.Currency,
            merchant_code = o.MerchantCode,
            description = request.Description,
            redirect_url = request.RedirectUrl.ToString(),
            return_url = request.WebhookUrl.ToString(), // SumUp calls this server-to-server when the status changes
            hosted_checkout = new { enabled = true },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v0.1/checkouts")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        Authorize(message, o);

        using var response = await http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("SumUp refused to create checkout {Reference}: {Status} {Body}",
                request.CheckoutReference, (int)response.StatusCode, error);
            throw new SumUpException($"SumUp returned {(int)response.StatusCode} when creating a checkout.");
        }

        var checkout = await response.Content.ReadFromJsonAsync<SumUpCheckout>(Json, ct)
                       ?? throw new SumUpException("SumUp returned an empty checkout.");

        if (string.IsNullOrEmpty(checkout.HostedCheckoutUrl))
            throw new SumUpException("SumUp did not return a hosted checkout URL. Check that Hosted Checkout is available on the account.");

        return checkout;
    }

    public async Task<SumUpCheckout?> GetCheckoutAsync(string checkoutId, CancellationToken ct = default)
    {
        var o = options.Value;
        EnsureConfigured(o);

        using var message = new HttpRequestMessage(HttpMethod.Get, $"v0.1/checkouts/{Uri.EscapeDataString(checkoutId)}");
        Authorize(message, o);

        using var response = await http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("SumUp returned {Status} when reading checkout {CheckoutId}", (int)response.StatusCode, checkoutId);
            throw new SumUpException($"SumUp returned {(int)response.StatusCode} when reading a checkout.");
        }

        return await response.Content.ReadFromJsonAsync<SumUpCheckout>(Json, ct);
    }

    private static void Authorize(HttpRequestMessage message, SumUpOptions o) =>
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

    private static void EnsureConfigured(SumUpOptions o)
    {
        if (!o.IsConfigured)
            throw new SumUpException("SumUp is not configured. Set SumUp:ApiKey and SumUp:MerchantCode.");
    }
}

public sealed class SumUpException(string message) : Exception(message);
