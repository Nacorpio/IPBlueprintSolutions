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
        Assert.Equal(firstCheckout, saved.ExtraPaymentCheckoutId);
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

        Assert.Null((await ReloadAsync(order.Id)).ExtraPaymentCheckoutId);
        Assert.DoesNotContain(_factory.Emails.Sent, e => e.Subject.StartsWith($"Kolla betalningen för {order.Reference}"));
    }
}
