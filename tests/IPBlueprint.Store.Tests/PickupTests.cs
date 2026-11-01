using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPBlueprint.Store.Tests;

/// <summary>Free pickup in Smedjebacken: no address, no shipping cost, and its own "ready for pickup" step.</summary>
public partial class PickupTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public PickupTests(StoreFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> PostCheckoutAsync(Product product, Dictionary<string, string> fields)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:1");
        var form = await client.GetStringAsync("/Checkout");
        fields["__RequestVerificationToken"] = AntiforgeryToken().Match(form).Groups[1].Value;
        return await client.PostAsync("/Checkout", new FormUrlEncodedContent(fields));
    }

    private static Dictionary<string, string> Customer(string email, string delivery) => new()
    {
        ["Input.Delivery"] = delivery,
        ["Input.Name"] = "Anna Andersson",
        ["Input.Email"] = email,
        ["Input.AcceptTerms"] = "true",
    };

    private Task<Order> OrderForAsync(string email) =>
        _factory.WithDbAsync(db => db.Orders.Include(o => o.Lines).SingleAsync(o => o.Email == email));

    private async Task PayAsync(Order order)
    {
        _factory.SumUp.SetStatus(order.SumUpCheckoutId!, "PAID");
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/webhooks/sumup", new { event_type = "CHECKOUT_STATUS_CHANGED", id = order.SumUpCheckoutId })).EnsureSuccessStatusCode();
    }

    private async Task<StatusChangeResult> ChangeAsync(Guid orderId, OrderStatus to)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderService>().ChangeStatusAsync(orderId, new StatusChangeRequest(to));
    }

    private async Task<Order> PaidPickupOrderAsync()
    {
        var product = await _factory.AddProductAsync("Hämtvara", 100m);
        var email = $"pickup-{Guid.NewGuid():N}@example.se";
        var response = await PostCheckoutAsync(product, Customer(email, "Pickup"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var order = await OrderForAsync(email);
        await PayAsync(order);
        return order;
    }

    [Fact]
    public void Pickup_is_always_free_and_shipping_still_costs()
    {
        var options = new ShopOptions { ShippingFlatRate = 69, FreeShippingFrom = 600 };

        Assert.Equal(0, Pricing.ShippingFor(100, options, DeliveryMethod.Pickup));
        Assert.Equal(69, Pricing.ShippingFor(100, options, DeliveryMethod.Shipping));
    }

    [Fact]
    public async Task Checkout_offers_pickup_in_Smedjebacken()
    {
        var product = await _factory.AddProductAsync("Visningsvara", 100m);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:1");

        var html = await client.GetStringAsync("/Checkout");

        Assert.Contains("Hämta själv i Smedjebacken", html);
        Assert.Contains("value=\"Pickup\"", html);
    }

    [Fact]
    public async Task Pickup_order_needs_no_address_and_has_no_shipping_cost()
    {
        var product = await _factory.AddProductAsync("Hämtkruka", 100m);
        var email = $"anna-{Guid.NewGuid():N}@example.se";

        var response = await PostCheckoutAsync(product, Customer(email, "Pickup"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://checkout.sumup.test/", response.Headers.Location!.AbsoluteUri);
        var order = await OrderForAsync(email);
        Assert.Equal(DeliveryMethod.Pickup, order.Delivery);
        Assert.Equal(0, order.ShippingCost);
        Assert.Equal(100m, order.Total);
        Assert.Null(order.AddressLine1);
        Assert.Null(order.City);
    }

    [Fact]
    public async Task Pickup_order_ignores_an_address_that_was_filled_in_before_switching()
    {
        var product = await _factory.AddProductAsync("Bytvara", 100m);
        var email = $"switch-{Guid.NewGuid():N}@example.se";
        var fields = Customer(email, "Pickup");
        fields["Input.AddressLine1"] = "Storgatan 1";
        fields["Input.PostalCode"] = "inte ett postnummer";

        var response = await PostCheckoutAsync(product, fields);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null((await OrderForAsync(email)).AddressLine1);
    }

    [Fact]
    public async Task Shipping_still_needs_the_address()
    {
        var product = await _factory.AddProductAsync("Skickvara", 100m);
        var email = $"ship-{Guid.NewGuid():N}@example.se";

        var response = await PostCheckoutAsync(product, Customer(email, "Shipping"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Fyll i din gatuadress.", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.WithDbAsync(db => db.Orders.AnyAsync(o => o.Email == email)));
    }

    [Fact]
    public async Task Pickup_orders_go_ready_for_pickup_instead_of_shipped()
    {
        var order = await PaidPickupOrderAsync();

        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(order.Id, OrderStatus.Shipped));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, OrderStatus.ReadyForPickup));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, OrderStatus.Delivered));
    }

    [Fact]
    public void Shipped_orders_cannot_be_marked_ready_for_pickup()
    {
        Assert.False(OrderStatusFlow.CanChange(OrderStatus.Paid, OrderStatus.ReadyForPickup, DeliveryMethod.Shipping));
        Assert.True(OrderStatusFlow.CanChange(OrderStatus.Paid, OrderStatus.Shipped, DeliveryMethod.Shipping));
        Assert.Equal("Picked up", OrderStatusFlow.AdminLabel(OrderStatus.Delivered, DeliveryMethod.Pickup));
        Assert.Equal("Delivered", OrderStatusFlow.AdminLabel(OrderStatus.Delivered));
    }

    [Fact]
    public async Task Pickup_emails_say_we_will_agree_on_a_time_and_owners_get_no_address()
    {
        var order = await PaidPickupOrderAsync();

        var paid = Assert.Single(_factory.Emails.Sent, e => e.To == order.Email);
        Assert.Contains("redo att hämtas i Smedjebacken", paid.Body);
        Assert.Contains("Upphämtning i Smedjebacken", paid.Body);
        var owner = Assert.Single(_factory.Emails.Sent, e => e.To == "owner@shop.test" && e.Subject.Contains(order.Reference));
        Assert.Contains("hämtas", owner.Subject);
        Assert.DoesNotContain("Leveransadress", owner.Body);

        await ChangeAsync(order.Id, OrderStatus.ReadyForPickup);

        var ready = Assert.Single(_factory.Emails.Sent, e => e.To == order.Email && e.Subject.Contains("redo att hämtas"));
        Assert.Contains("vilka dagar och tider som passar dig", ready.Body);
    }

    [Fact]
    public async Task Order_page_shows_the_pickup_steps()
    {
        var order = await PaidPickupOrderAsync();
        await ChangeAsync(order.Id, OrderStatus.ReadyForPickup);

        var html = await _factory.CreateClient().GetStringAsync($"/order/{order.Id}");

        Assert.Contains("Din beställning är redo att hämtas", html);
        Assert.Contains("Redo att hämtas", html);
        Assert.Contains("Hämtad", html);
        Assert.DoesNotContain(">Skickad<", html);
        Assert.Contains("Upphämtning i Smedjebacken", html);
    }

    [Fact]
    public async Task Front_page_tells_search_engines_the_town_but_not_a_street_address()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        Assert.Contains("\"addressLocality\":\"Smedjebacken\"", html);
        Assert.Contains("\"addressRegion\":\"Dalarna\"", html);
        Assert.DoesNotContain("streetAddress", html);
        Assert.Contains("Fri upphämtning i Smedjebacken", html);
    }

    [Fact]
    public async Task Pickup_can_be_switched_off()
    {
        using var factory = new StoreFactory();
        var client = factory.WithWebHostBuilder(b => b.UseSetting("Shop:PickupEnabled", "false")).CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("Fri upphämtning", html);
    }

    [Fact]
    public async Task Custom_request_can_ask_for_pickup()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = await client.GetStringAsync("/Custom");
        Assert.Contains("Jag hämtar själv i Smedjebacken", form);
        var email = $"custom-{Guid.NewGuid():N}@example.se";

        var content = new MultipartFormDataContent
        {
            { new StringContent(AntiforgeryToken().Match(form).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent("Anna"), "Input.Name" },
            { new StringContent(email), "Input.Email" },
            { new StringContent("En liten skylt med vårt efternamn på."), "Input.Description" },
            { new StringContent("Pickup"), "Input.Delivery" },
        };
        var response = await client.PostAsync("/Custom", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var request = await _factory.WithDbAsync(db => db.CustomOrderRequests.SingleAsync(r => r.Email == email));
        Assert.Equal(DeliveryMethod.Pickup, request.Delivery);
        Assert.Contains(_factory.Emails.Sent, e => e.To == "owner@shop.test" && e.Body.Contains("Vill hämta i Smedjebacken"));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
