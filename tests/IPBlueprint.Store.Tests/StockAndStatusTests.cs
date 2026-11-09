using System.Net.Http.Json;
using System.Text.RegularExpressions;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Shop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPBlueprint.Store.Tests;

public partial class StockAndStatusTests : IClassFixture<StoreFactory>
{
    private readonly StoreFactory _factory;

    public StockAndStatusTests(StoreFactory factory) => _factory = factory;

    private Task<Product> AddProductAsync(string name, decimal price = 100m, int? stock = null) =>
        _factory.WithDbAsync(async db =>
        {
            var p = new Product { Name = name, Slug = Slug(name), Price = price, Stock = stock };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            return p;
        });

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Order> PlaceOrderAsync(Product product, int quantity = 1, string language = Languages.Swedish)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var subtotal = product.Price * quantity;
        var cart = new CartSummary([new CartLine(product, quantity)], subtotal, Pricing.ShippingFor(subtotal, new ShopOptions()));

        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = Languages.CultureFor(language);
        try
        {
            var order = await orders.CreateOrderAsync(cart, new CustomerDetails(
                "Anna Andersson", $"anna-{Guid.NewGuid():N}@example.se", null, "Storgatan 1", null, "123 45", "Stockholm", null));
            await orders.StartPaymentAsync(order, new Uri("https://shop.test/"));
            return order;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    private async Task PayAsync(Order order)
    {
        _factory.SumUp.SetStatus(order.SumUpCheckoutId!, "PAID");
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/webhooks/sumup", new { event_type = "CHECKOUT_STATUS_CHANGED", id = order.SumUpCheckoutId })).EnsureSuccessStatusCode();
    }

    private Task<int?> StockAsync(int productId) =>
        _factory.WithDbAsync(db => db.Products.Where(p => p.Id == productId).Select(p => p.Stock).SingleAsync());

    private async Task<StatusChangeResult> ChangeAsync(Guid orderId, StatusChangeRequest request)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderService>().ChangeStatusAsync(orderId, request);
    }

    private List<(string To, string Subject, string Body)> EmailsTo(string to) => _factory.Emails.Sent.Where(e => e.To == to).ToList();

    // ---- Stock ----

    [Fact]
    public async Task Paid_order_takes_items_out_of_stock_once_even_if_SumUp_calls_twice()
    {
        var product = await AddProductAsync("Vas", stock: 5);
        var order = await PlaceOrderAsync(product, quantity: 2);

        await PayAsync(order);
        await PayAsync(order); // SumUp may send the webhook again

        Assert.Equal(3, await StockAsync(product.Id));
    }

    [Fact]
    public async Task Concurrent_confirmations_take_stock_once()
    {
        var product = await AddProductAsync("Lampa", stock: 4);
        var order = await PlaceOrderAsync(product, quantity: 1);
        _factory.SumUp.SetStatus(order.SumUpCheckoutId!, "PAID");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrderService>().SyncCheckoutAsync(order.SumUpCheckoutId!);
        }));

        Assert.Equal(3, await StockAsync(product.Id));
    }

    [Fact]
    public async Task Made_to_order_products_have_no_stock_to_take()
    {
        var product = await AddProductAsync("Drake");
        await PayAsync(await PlaceOrderAsync(product, quantity: 3));

        Assert.Null(await StockAsync(product.Id));
    }

    [Fact]
    public async Task Selling_more_than_in_stock_stops_at_zero_and_warns_the_owners()
    {
        var product = await AddProductAsync("Sista skålen", stock: 1);
        var order = await PlaceOrderAsync(product, quantity: 2);

        await PayAsync(order);

        Assert.Equal(0, await StockAsync(product.Id));
        var owner = Assert.Single(EmailsTo("owner@shop.test"), e => e.Subject.Contains(order.Reference));
        Assert.Contains("Lagret räckte inte", owner.Body);
    }

    [Fact]
    public async Task Sold_out_product_shows_sold_out_and_cannot_be_added_to_the_cart()
    {
        var product = await AddProductAsync("Slutsåld kruka", stock: 0);
        var client = _factory.CreateClient();

        var page = await client.GetStringAsync($"/products/{product.Slug}");
        Assert.Contains("Slutsåld", page);
        Assert.Contains("Mejla mig", page);
        Assert.DoesNotContain("Lägg i varukorgen", page);

        var response = await client.PostAsync($"/products/{product.Slug}", Form(Token(page), ("quantity", "1")));
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("cart=") && c.Contains($"{product.Id}:"));
    }

    [Fact]
    public async Task Low_stock_says_how_many_are_left()
    {
        var product = await AddProductAsync("Nästan slut", stock: 2);

        Assert.Contains("Endast 2 kvar", await _factory.CreateClient().GetStringAsync($"/products/{product.Slug}"));
        Assert.Contains("Only 2 left", await _factory.CreateClient().GetStringAsync($"/products/{product.Slug}?lang=en"));
    }

    [Fact]
    public async Task Cart_shrinks_to_what_is_in_stock_and_says_so()
    {
        var product = await AddProductAsync("Tre kvar", stock: 3);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:8");

        var html = await client.GetStringAsync("/Cart");

        Assert.Contains("Vi har bara 3 × Tre kvar kvar", html);
        Assert.Contains("value=\"3\"", html);
    }

    [Fact]
    public async Task Header_cart_preview_lists_what_is_in_the_cart_without_changing_it()
    {
        var product = await AddProductAsync("Liten vas", price: 120m, stock: 3);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:5");

        var response = await client.GetAsync("/gallery");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("class=\"cart-preview\"", html);
        Assert.Contains("Liten vas", html);
        Assert.Contains("3 × 120 kr", html); // capped at what's in stock, like the cart page
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("cart=")));
    }

    [Fact]
    public async Task Cart_page_has_no_header_preview()
    {
        var product = await AddProductAsync("Skål", stock: 3);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"cart={product.Id}:1");

        Assert.DoesNotContain("class=\"cart-preview\"", await client.GetStringAsync("/Cart"));
        Assert.DoesNotContain("class=\"cart-preview\"", await _factory.CreateClient().GetStringAsync("/gallery"));
    }

    [Fact]
    public async Task People_who_ask_are_emailed_once_when_the_product_is_back()
    {
        var product = await AddProductAsync("Popular figur", stock: 0);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<StockAlerts>();
            Assert.Equal(StockAlertResult.Added, await alerts.SubscribeAsync(product, "Kim@Example.se", "en"));
            Assert.Equal(StockAlertResult.AlreadyAdded, await alerts.SubscribeAsync(product, "kim@example.se", "en"));
            Assert.Equal(StockAlertResult.InvalidEmail, await alerts.SubscribeAsync(product, "not an email", "sv"));
            Assert.Equal(0, await alerts.NotifyIfAvailableAsync(product.Id)); // still sold out
        }

        await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 4)));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<StockAlerts>();
            Assert.Equal(1, await alerts.NotifyIfAvailableAsync(product.Id));
            Assert.Equal(0, await alerts.NotifyIfAvailableAsync(product.Id)); // address is gone after the first email
        }

        var email = Assert.Single(EmailsTo("kim@example.se"));
        Assert.Equal("Popular figur is back in stock", email.Subject);
        Assert.Contains($"https://shop.test/products/{product.Slug}?lang=en", email.Body);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.StockAlerts.CountAsync(a => a.ProductId == product.Id)));
    }

    [Fact]
    public async Task Visitors_can_sign_up_for_the_back_in_stock_email_on_the_product_page()
    {
        var product = await AddProductAsync("Väntelista", stock: 0);
        var client = _factory.CreateClient();
        var page = await client.GetStringAsync($"/products/{product.Slug}");

        var response = await client.PostAsync($"/products/{product.Slug}?handler=Notify", Form(Token(page), ("email", "vanta@example.se")));

        Assert.Contains("Tack! Vi mejlar vanta@example.se när den finns igen.", await response.Content.ReadAsStringAsync());
        Assert.True(await _factory.WithDbAsync(db => db.StockAlerts.AnyAsync(a => a.ProductId == product.Id && a.Email == "vanta@example.se")));
    }

    [Fact]
    public async Task Admin_saving_a_product_keeps_sales_made_while_the_form_was_open()
    {
        var product = await AddProductAsync("Admin vas", stock: 5);
        var client = await AdminClientAsync();
        var form = await client.GetStringAsync($"/Admin/Products/Edit/{product.Id}");

        // A sale comes in while the admin is editing the description.
        await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 4)));

        await client.PostAsync($"/Admin/Products/Edit/{product.Id}", Form(Token(form),
            ("Input.Name", product.Name), ("Input.Price", "100"), ("Input.MadeToOrder", "false"), ("Input.Stock", "5"),
            ("Input.OriginalStock", "5"), ("Input.IsActive", "true"), ("Input.Description", "Ny text")));
        Assert.Equal(4, await StockAsync(product.Id));

        // Changing the number itself does save it.
        await client.PostAsync($"/Admin/Products/Edit/{product.Id}", Form(Token(form),
            ("Input.Name", product.Name), ("Input.Price", "100"), ("Input.MadeToOrder", "false"), ("Input.Stock", "10"),
            ("Input.OriginalStock", "4"), ("Input.IsActive", "true")));
        Assert.Equal(10, await StockAsync(product.Id));
    }

    // ---- Order statuses ----

    [Fact]
    public async Task Status_changes_email_the_customer_in_their_language_with_a_link_to_the_order_page()
    {
        var order = await PlaceOrderAsync(await AddProductAsync("Statusvas"), language: Languages.English);
        await PayAsync(order);

        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, new(OrderStatus.InProduction)));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, new(OrderStatus.Shipped, TrackingNumber: " RR123456785SE ")));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, new(OrderStatus.Delivered)));

        var subjects = EmailsTo(order.Email).Select(e => e.Subject).ToList();
        Assert.Equal(
            [$"Thank you for your order {order.Reference}", $"We're printing your order {order.Reference}",
             $"Your order {order.Reference} is on its way", $"Your order {order.Reference} has been delivered"],
            subjects);
        Assert.All(EmailsTo(order.Email), e => Assert.Contains($"https://shop.test/order/{order.Id}?lang=en", e.Body));
        Assert.Contains("RR123456785SE", EmailsTo(order.Email)[2].Body);

        var saved = await _factory.WithDbAsync(db => db.Orders.Include(o => o.History).SingleAsync(o => o.Id == order.Id));
        Assert.Equal(OrderStatus.Delivered, saved.Status);
        Assert.Equal("RR123456785SE", saved.TrackingNumber);
        Assert.Equal([OrderStatus.Paid, OrderStatus.InProduction, OrderStatus.Shipped, OrderStatus.Delivered],
            saved.History.OrderBy(h => h.AtUtc).ThenBy(h => h.Id).Select(h => h.Status));
    }

    [Fact]
    public async Task Only_the_next_steps_are_allowed()
    {
        var unpaid = await PlaceOrderAsync(await AddProductAsync("Obetald"));
        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(unpaid.Id, new(OrderStatus.Shipped)));
        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(unpaid.Id, new(OrderStatus.Refunded)));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(unpaid.Id, new(OrderStatus.Cancelled)));
        Assert.Empty(EmailsTo(unpaid.Email)); // cancelling an unpaid order sends nothing

        var paid = await PlaceOrderAsync(await AddProductAsync("Betald"));
        await PayAsync(paid);
        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(paid.Id, new(OrderStatus.Delivered)));
        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(paid.Id, new(OrderStatus.Cancelled)));
        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(paid.Id, new(OrderStatus.InProduction, EmailCustomer: false)));
        Assert.Single(EmailsTo(paid.Email)); // only the order confirmation
    }

    [Fact]
    public async Task Refund_can_put_items_back_in_stock_once_and_tells_people_waiting()
    {
        var product = await AddProductAsync("Återbetald lampa", stock: 1);
        var order = await PlaceOrderAsync(product);
        await PayAsync(order);
        Assert.Equal(0, await StockAsync(product.Id));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var soldOut = await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Products.SingleAsync(p => p.Id == product.Id);
            await scope.ServiceProvider.GetRequiredService<StockAlerts>().SubscribeAsync(soldOut, "waiting@example.se", "sv");
        }

        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, new(OrderStatus.Refunded, Restock: true)));
        Assert.Equal(1, await StockAsync(product.Id));
        Assert.Contains(EmailsTo(order.Email), e => e.Subject == $"Din beställning {order.Reference} har återbetalats");
        Assert.Contains(EmailsTo("waiting@example.se"), e => e.Subject == "Återbetald lampa finns i lager igen");
        Assert.Equal(StatusChangeResult.NotAllowed, await ChangeAsync(order.Id, new(OrderStatus.Refunded, Restock: true)));
        Assert.Equal(1, await StockAsync(product.Id));
    }

    [Fact]
    public async Task Refund_after_a_stock_shortage_puts_back_only_what_was_taken()
    {
        var product = await AddProductAsync("Sista vasen", stock: 1);
        var first = await PlaceOrderAsync(product);
        var second = await PlaceOrderAsync(product);
        await PayAsync(first);
        await PayAsync(second); // nothing left to take: the owners are warned about the shortage
        Assert.Equal(0, await StockAsync(product.Id));

        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(second.Id, new(OrderStatus.Refunded, Restock: true)));

        Assert.Equal(0, await StockAsync(product.Id));
    }

    [Fact]
    public async Task Refund_of_a_product_that_was_made_to_order_when_sold_adds_no_stock()
    {
        var product = await AddProductAsync("Beställd figur"); // made to order
        var order = await PlaceOrderAsync(product, quantity: 2);
        await PayAsync(order);
        await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 3))); // later given a stock count

        Assert.Equal(StatusChangeResult.Changed, await ChangeAsync(order.Id, new(OrderStatus.Refunded, Restock: true)));

        Assert.Equal(3, await StockAsync(product.Id));
    }

    [Fact]
    public async Task Order_page_shows_where_the_order_is()
    {
        var order = await PlaceOrderAsync(await AddProductAsync("Spårad vas"));
        await PayAsync(order);
        await ChangeAsync(order.Id, new(OrderStatus.Shipped, TrackingNumber: "RR999SE"));

        var html = await _factory.CreateClient().GetStringAsync($"/order/{order.Id}");

        Assert.Contains("Din beställning är på väg", html);
        Assert.Contains("RR999SE", html);
        Assert.Matches(@"<li class=""done""[^>]*>\s*<span class=""dot""[^>]*></span>\s*<span class=""label"">Betald</span>", html);
        Assert.Contains("<li class=\"done current\" aria-current=\"step\">", html);
        Assert.Contains("noindex", html);
    }

    [Fact]
    public async Task Admin_changes_status_from_the_order_page()
    {
        var order = await PlaceOrderAsync(await AddProductAsync("Adminorder"));
        await PayAsync(order);
        var client = await AdminClientAsync();
        var page = await client.GetStringAsync($"/Admin/Orders/Details/{order.Id}");
        Assert.Contains("Mark as in production", page);

        var response = await client.PostAsync($"/Admin/Orders/Details/{order.Id}?handler=Status",
            Form(Token(page), ("to", "InProduction"), ("emailCustomer", "true")));

        Assert.Contains("Marked as in production. The customer has been emailed.", await response.Content.ReadAsStringAsync());
        Assert.Contains(EmailsTo(order.Email), e => e.Subject.StartsWith("Nu printar vi din beställning"));
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var token = Token(await client.GetStringAsync("/Admin/Login"));
        (await client.PostAsync("/Admin/Login", Form(token, ("password", "correct horse battery staple")))).EnsureSuccessStatusCode();
        return client;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);

    private static string Token(string html) => AntiforgeryToken().Match(html).Groups[1].Value;

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
