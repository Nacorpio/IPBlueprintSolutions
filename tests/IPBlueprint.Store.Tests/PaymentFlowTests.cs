using System.Net;
using System.Net.Http.Json;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPBlueprint.Store.Tests;

public class PaymentFlowTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public PaymentFlowTests(StoreFactory factory) => _factory = factory;

    private async Task<(Order Order, string CheckoutId)> PlaceOrderAsync(decimal price = 249m, int quantity = 1)
    {
        var product = await _factory.AddProductAsync("Planter", price);

        await using var scope = _factory.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var options = new ShopOptions();

        var subtotal = price * quantity;
        var cart = new CartSummary([new CartLine(product, quantity)], subtotal, Pricing.ShippingFor(subtotal, options));
        var order = await orders.CreateOrderAsync(cart, new CustomerDetails(
            "Anna Andersson", "anna@example.se", null, "Storgatan 1", null, "123 45", "Stockholm", null));
        await orders.StartPaymentAsync(order, new Uri("https://shop.test/"));

        return (order, order.SumUpCheckoutId!);
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string checkoutId)
    {
        var client = _factory.CreateClient();
        return await client.PostAsJsonAsync("/webhooks/sumup", new { event_type = "CHECKOUT_STATUS_CHANGED", id = checkoutId });
    }

    private Task<Order> ReloadAsync(Guid id) =>
        _factory.WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == id));

    private Task<List<PaymentIssue>> IssuesAsync(Guid orderId) =>
        _factory.WithDbAsync(db => db.PaymentIssues.AsNoTracking().Where(i => i.OrderId == orderId).ToListAsync());

    private static string Token(string html) => System.Text.RegularExpressions.Regex.Match(html,
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var token = Token(await client.GetStringAsync("/Admin/Login"));
        (await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("password", "correct horse battery staple")]))).EnsureSuccessStatusCode();
        return client;
    }

    private int EmailsAbout(string reference) => _factory.Emails.Sent.Count(e => e.Subject.Contains(reference));

    [Fact]
    public async Task Checkout_is_created_with_order_total_and_shop_urls()
    {
        var (order, checkoutId) = await PlaceOrderAsync(price: 249m, quantity: 2);

        var request = _factory.SumUp.Created.Single(c => c.CheckoutReference == order.Reference);
        Assert.Equal(249m * 2 + 69m, request.Amount);
        Assert.Equal("SEK", request.Currency);
        Assert.Equal($"https://shop.test/order/{order.Id}", request.RedirectUrl.ToString());
        Assert.Equal("https://shop.test/webhooks/sumup", request.WebhookUrl.ToString());
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order.Id)).Status);
        Assert.NotNull(checkoutId);
    }

    [Fact]
    public async Task Paid_webhook_marks_order_paid_and_emails_customer_and_owner_once()
    {
        var (order, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(checkoutId, "PAID");

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(checkoutId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(checkoutId)).StatusCode); // SumUp may send it again

        var saved = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Paid, saved.Status);
        Assert.NotNull(saved.PaidAtUtc);
        Assert.Equal("TX123", saved.SumUpTransactionCode);
        Assert.Equal(2, EmailsAbout(order.Reference)); // one to the customer, one to the owner
    }

    [Fact]
    public async Task Pending_webhook_does_not_mark_order_paid()
    {
        var (order, checkoutId) = await PlaceOrderAsync();

        await PostWebhookAsync(checkoutId);

        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order.Id)).Status);
        Assert.Equal(0, EmailsAbout(order.Reference));
    }

    [Fact]
    public async Task Paid_checkout_with_wrong_amount_is_not_accepted()
    {
        var (order, checkoutId) = await PlaceOrderAsync(price: 500m);
        _factory.SumUp.SetStatus(checkoutId, "PAID", amount: 1m);

        await PostWebhookAsync(checkoutId);

        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Failed_payment_can_be_retried_with_a_new_checkout()
    {
        var (order, firstCheckout) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(firstCheckout, "FAILED");
        await PostWebhookAsync(firstCheckout);
        Assert.Equal(OrderStatus.PaymentFailed, (await ReloadAsync(order.Id)).Status);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var tracked = await db.Orders.SingleAsync(o => o.Id == order.Id);
        await scope.ServiceProvider.GetRequiredService<OrderService>().StartPaymentAsync(tracked, new Uri("https://shop.test/"));

        Assert.Contains(_factory.SumUp.Created, c => c.CheckoutReference == order.Reference + "-2");

        _factory.SumUp.SetStatus(tracked.SumUpCheckoutId!, "PAID");
        await PostWebhookAsync(tracked.SumUpCheckoutId!);
        Assert.Equal(OrderStatus.Paid, (await ReloadAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Unknown_checkout_is_ignored()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync("does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Return_page_confirms_payment_even_before_the_webhook_arrives()
    {
        var (order, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(checkoutId, "PAID");

        var html = await _factory.CreateClient().GetStringAsync($"/order/{order.Id}");

        Assert.Contains("Tack för din beställning", html);
        Assert.Equal(OrderStatus.Paid, (await ReloadAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Paid_emails_still_go_out_when_the_visitor_leaves_while_they_are_sending()
    {
        var (order, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(checkoutId, "PAID");
        using var visitorLeaves = new CancellationTokenSource();
        _factory.Emails.BeforeSend = visitorLeaves.Cancel; // the tab is closed as soon as the first email starts

        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<OrderService>().SyncCheckoutAsync(checkoutId, visitorLeaves.Token);
            Assert.Equal(PaymentSyncResult.MarkedPaid, result);
        }
        finally
        {
            _factory.Emails.BeforeSend = null;
        }

        Assert.Equal(2, EmailsAbout(order.Reference)); // the customer's confirmation and the owner's new-order email
    }

    [Fact]
    public async Task Paying_a_second_checkout_for_a_paid_order_tells_the_owners_once()
    {
        var (order, firstCheckout) = await PlaceOrderAsync();

        // The customer used "Pay now" while the first payment page was still open, and then paid both.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var tracked = await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Orders.SingleAsync(o => o.Id == order.Id);
            await scope.ServiceProvider.GetRequiredService<OrderService>().StartPaymentAsync(tracked, new Uri("https://shop.test/"));
        }
        var secondCheckout = (await ReloadAsync(order.Id)).SumUpCheckoutId!;
        _factory.SumUp.SetStatus(secondCheckout, "PAID");
        await PostWebhookAsync(secondCheckout);
        _factory.SumUp.SetStatus(firstCheckout, "PAID");

        await PostWebhookAsync(firstCheckout);
        await PostWebhookAsync(firstCheckout); // SumUp may send it again

        var saved = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Paid, saved.Status);
        Assert.Equal(secondCheckout, saved.SumUpCheckoutId);
        var issue = Assert.Single(await IssuesAsync(order.Id));
        Assert.Equal((firstCheckout, PaymentIssueKind.DoubleCharge), (issue.CheckoutId, issue.Kind));
        var alert = Assert.Single(_factory.Emails.Sent, e => e.Subject.StartsWith($"Kolla betalningen för {order.Reference}"));
        Assert.Equal("owner@shop.test", alert.To);
        Assert.Contains(firstCheckout, alert.Body);
    }

    [Fact]
    public async Task The_same_checkout_reported_again_is_not_a_second_payment()
    {
        var (order, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(checkoutId, "PAID");

        await PostWebhookAsync(checkoutId);
        await PostWebhookAsync(checkoutId);

        Assert.Empty(await IssuesAsync(order.Id));
        Assert.DoesNotContain(_factory.Emails.Sent, e => e.Subject.StartsWith($"Kolla betalningen för {order.Reference}"));
    }

    [Fact]
    public async Task Paying_the_first_checkout_while_pay_now_runs_keeps_the_order_on_the_paid_checkout()
    {
        var (order, firstCheckout) = await PlaceOrderAsync();
        // The customer pays the first payment page in another tab while "Pay now" is waiting for SumUp.
        _factory.SumUp.BeforeCreate = async () =>
        {
            _factory.SumUp.SetStatus(firstCheckout, "PAID");
            await PostWebhookAsync(firstCheckout);
        };

        string? paymentUrl;
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var tracked = await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Orders.SingleAsync(o => o.Id == order.Id);
            paymentUrl = await scope.ServiceProvider.GetRequiredService<OrderService>().StartPaymentAsync(tracked, new Uri("https://shop.test/"));
        }
        finally
        {
            _factory.SumUp.BeforeCreate = null;
        }

        Assert.Null(paymentUrl); // nothing left to pay, so the customer isn't sent to the new checkout
        var saved = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Paid, saved.Status);
        Assert.Equal(firstCheckout, saved.SumUpCheckoutId);

        // If they still manage to pay the new checkout, the owners hear about it.
        var secondCheckout = _factory.SumUp.Checkouts.Values.Single(c => c.CheckoutReference == order.Reference + "-2").Id;
        _factory.SumUp.SetStatus(secondCheckout, "PAID");
        await PostWebhookAsync(secondCheckout);
        Assert.Equal(secondCheckout, Assert.Single(await IssuesAsync(order.Id)).CheckoutId);
    }

    [Fact]
    public async Task A_payment_with_the_wrong_amount_tells_the_owners_once_and_stops_the_customer_paying_again()
    {
        var (order, checkoutId) = await PlaceOrderAsync(price: 300m);
        _factory.SumUp.SetStatus(checkoutId, "PAID", amount: 3m);

        await PostWebhookAsync(checkoutId);
        await PostWebhookAsync(checkoutId); // SumUp may send it again

        var alert = Assert.Single(_factory.Emails.Sent, e => e.Subject == $"Kolla betalningen för {order.Reference}: fel belopp");
        Assert.Equal("owner@shop.test", alert.To);
        Assert.Contains(checkoutId, alert.Body);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync($"/order/{order.Id}");
        Assert.Contains("Vi kontrollerar din betalning", html);
        Assert.DoesNotContain("handler=Retry", html);

        // Even a hand-made "Pay now" post doesn't create a new checkout.
        var token = System.Text.RegularExpressions.Regex.Match(await client.GetStringAsync("/Custom"),
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var created = _factory.SumUp.Created.Count(c => c.CheckoutReference.StartsWith(order.Reference));
        var retry = await client.PostAsync($"/order/{order.Id}?handler=Retry",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal($"/order/{order.Id}", retry.Headers.Location?.OriginalString);
        Assert.Equal(created, _factory.SumUp.Created.Count(c => c.CheckoutReference.StartsWith(order.Reference)));
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order.Id)).Status);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task Webhook_asks_SumUp_to_retry_when_SumUp_cannot_be_reached(string failure)
    {
        var (_, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.ReadFailure = failure == "http" ? new HttpRequestException("Connection refused") : new TaskCanceledException("Timed out");
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostWebhookAsync(checkoutId)).StatusCode);
        }
        finally
        {
            _factory.SumUp.ReadFailure = null;
        }
    }

    [Fact]
    public async Task Resolving_a_wrong_amount_payment_lets_the_customer_pay_again()
    {
        var (order, checkoutId) = await PlaceOrderAsync(price: 300m);
        _factory.SumUp.SetStatus(checkoutId, "PAID", amount: 3m);
        await PostWebhookAsync(checkoutId);
        var issue = Assert.Single(await IssuesAsync(order.Id));
        Assert.Equal(PaymentIssueKind.WrongAmount, issue.Kind);
        Assert.Equal(3m, issue.Amount);

        var admin = await AdminClientAsync();
        var adminPage = await admin.GetStringAsync($"/Admin/Orders/Details/{order.Id}");
        Assert.Contains("Mark resolved", adminPage);
        var resolved = await admin.PostAsync($"/Admin/Orders/Details/{order.Id}?handler=ResolveIssue", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Token(adminPage)), new("issueId", issue.Id.ToString()), new("note", "Refunded in SumUp")]));
        Assert.Contains("Marked the payment issue as resolved.", await resolved.Content.ReadAsStringAsync());

        var saved = Assert.Single(await IssuesAsync(order.Id));
        Assert.NotNull(saved.ResolvedAtUtc);
        Assert.Equal("Refunded in SumUp", saved.ResolutionNote);
        Assert.Equal(OrderStatus.AwaitingPayment, (await ReloadAsync(order.Id)).Status); // resolving leaves the status alone

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync($"/order/{order.Id}");
        Assert.DoesNotContain("Vi kontrollerar din betalning", html);
        Assert.Contains("handler=Retry", html);
        var retry = await client.PostAsync($"/order/{order.Id}?handler=Retry",
            new FormUrlEncodedContent([new("__RequestVerificationToken", Token(html))]));
        Assert.StartsWith("https://checkout.sumup.test/pay/", retry.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Pay_now_creates_no_checkout_while_a_wrong_amount_payment_is_open()
    {
        var (order, checkoutId) = await PlaceOrderAsync(price: 300m);
        _factory.SumUp.SetStatus(checkoutId, "PAID", amount: 3m);
        await PostWebhookAsync(checkoutId);

        // Straight to the service, as if the mismatch was recorded while this "Pay now" was already past the page's check.
        await using var scope = _factory.Services.CreateAsyncScope();
        var tracked = await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Orders.SingleAsync(o => o.Id == order.Id);
        var created = _factory.SumUp.Created.Count;

        Assert.Null(await scope.ServiceProvider.GetRequiredService<OrderService>().StartPaymentAsync(tracked, new Uri("https://shop.test/")));
        Assert.Equal(created, _factory.SumUp.Created.Count);
    }

    [Fact]
    public async Task A_second_stray_payment_is_kept_next_to_the_first()
    {
        var (order, firstCheckout) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(firstCheckout, "PAID");
        await PostWebhookAsync(firstCheckout);

        // Two more checkouts for the same order get paid (say, two old "Pay now" tabs).
        var strays = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var stray = await _factory.SumUp.CreateCheckoutAsync(new Payments.CreateCheckoutRequest(
                $"{order.Reference}-{i + 5}", order.Total, "SEK", "", new Uri("https://shop.test/"), new Uri("https://shop.test/")));
            _factory.SumUp.SetStatus(stray.Id, "PAID");
            await PostWebhookAsync(stray.Id);
            strays.Add(stray.Id);
        }

        var issues = await IssuesAsync(order.Id);
        Assert.Equal(strays.Order(), issues.Select(i => i.CheckoutId).Order());
        Assert.All(issues, i => Assert.Equal(PaymentIssueKind.DoubleCharge, i.Kind));
    }

    [Fact]
    public async Task A_payment_that_lands_as_the_admin_cancels_is_recorded_for_the_owners()
    {
        var (order, checkoutId) = await PlaceOrderAsync();
        _factory.SumUp.SetStatus(checkoutId, "PAID");
        var cancelFirst = new CancelBeforeMarkingPaid(order.Id);
        await using var app = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.ConfigureDbContext<StoreDbContext>(o => o.AddInterceptors(cancelFirst))));

        var response = await app.CreateClient().PostAsJsonAsync("/webhooks/sumup", new { event_type = "CHECKOUT_STATUS_CHANGED", id = checkoutId });

        response.EnsureSuccessStatusCode();
        Assert.True(SpinWait.SpinUntil(() => app.Services.GetRequiredService<BackgroundWorkQueue>().IsIdle, TimeSpan.FromSeconds(10)));
        Assert.True(cancelFirst.Fired);
        Assert.Equal(OrderStatus.Cancelled, (await ReloadAsync(order.Id)).Status);
        var issue = Assert.Single(await IssuesAsync(order.Id));
        Assert.Equal((checkoutId, PaymentIssueKind.PaidAfterCancel), (issue.CheckoutId, issue.Kind));
        Assert.Contains(_factory.Emails.Sent, e => e.Subject.StartsWith($"Kolla betalningen för {order.Reference}"));
    }

    /// <summary>Cancels the order (as an admin would, in its own committed change) after the payment has read the order
    /// but before it starts the transaction that marks it paid.</summary>
    private sealed class CancelBeforeMarkingPaid(Guid orderId) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(
            System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionStartingEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await using var cancel = connection.CreateCommand();
                cancel.CommandText = "UPDATE \"Orders\" SET \"Status\" = 'Cancelled' WHERE \"Id\" = $id";
                var id = cancel.CreateParameter();
                id.ParameterName = "$id";
                id.Value = orderId.ToString().ToUpperInvariant();
                cancel.Parameters.Add(id);
                await cancel.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }
}
