using System.Security.Cryptography;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Shop;

public sealed record CustomerDetails(
    string Name,
    string Email,
    string? Phone,
    string AddressLine1,
    string? AddressLine2,
    string PostalCode,
    string City,
    string? Note);

public enum PaymentSyncResult
{
    /// <summary>The checkout is not one of ours, or SumUp does not know it.</summary>
    Unknown,
    StillPending,
    MarkedPaid,
    AlreadyPaid,
    MarkedFailed,
    /// <summary>SumUp says paid, but the amount or currency doesn't match the order. Needs a human look.</summary>
    AmountMismatch,
}

public sealed class OrderService(
    StoreDbContext db,
    ISumUpClient sumUp,
    IOptions<SumUpOptions> sumUpOptions,
    OrderNotifications notifications,
    TimeProvider clock,
    ILogger<OrderService> logger)
{
    private static readonly OrderStatus[] Payable = [OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed];

    public async Task<Order> CreateOrderAsync(CartSummary cart, CustomerDetails customer, CancellationToken ct = default)
    {
        if (cart.IsEmpty) throw new InvalidOperationException("The cart is empty.");

        var order = new Order
        {
            Reference = NewReference(clock.GetUtcNow()),
            CustomerName = customer.Name.Trim(),
            Email = customer.Email.Trim(),
            Phone = customer.Phone?.Trim(),
            AddressLine1 = customer.AddressLine1.Trim(),
            AddressLine2 = customer.AddressLine2?.Trim(),
            PostalCode = customer.PostalCode.Trim(),
            City = customer.City.Trim(),
            CustomerNote = customer.Note?.Trim(),
            Language = Languages.Current,
            Subtotal = cart.Subtotal,
            ShippingCost = cart.Shipping,
            Total = cart.Total,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            Lines = cart.Lines.Select(l => new OrderLine
            {
                ProductId = l.Product.Id,
                // The name the customer saw, so the order page and receipt read the same as the cart did.
                ProductName = l.Product.DisplayName,
                UnitPrice = l.Product.Price,
                Quantity = l.Quantity,
            }).ToList(),
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Creates a SumUp hosted checkout for the order and returns the URL to send the customer to.</summary>
    public async Task<string> StartPaymentAsync(Order order, Uri siteBaseUrl, CancellationToken ct = default)
    {
        if (!Payable.Contains(order.Status))
            throw new InvalidOperationException($"Order {order.Reference} cannot be paid in status {order.Status}.");

        order.PaymentAttempts++;

        // SumUp needs a unique reference per checkout, so retries get a suffix: IPB-261031-7KQ2, IPB-261031-7KQ2-2, ...
        var checkoutReference = order.PaymentAttempts == 1 ? order.Reference : $"{order.Reference}-{order.PaymentAttempts}";

        var checkout = await sumUp.CreateCheckoutAsync(new CreateCheckoutRequest(
            CheckoutReference: checkoutReference,
            Amount: order.Total,
            Currency: sumUpOptions.Value.Currency,
            Description: (order.Language == Languages.English ? "Order " : "Beställning ") + order.Reference,
            RedirectUrl: new Uri(siteBaseUrl, $"order/{order.Id}"),
            WebhookUrl: new Uri(siteBaseUrl, "webhooks/sumup")), ct);

        order.SumUpCheckoutId = checkout.Id;
        order.Status = OrderStatus.AwaitingPayment;
        await db.SaveChangesAsync(ct);

        return checkout.HostedCheckoutUrl!;
    }

    /// <summary>
    /// Asks SumUp for the real state of a checkout and updates the matching order.
    /// SumUp webhooks are not signed, so the webhook body is only a hint; this call is the source of truth.
    /// Safe to call many times and from several places at once: only the first caller that marks an order paid sends emails.
    /// </summary>
    public async Task<PaymentSyncResult> SyncCheckoutAsync(string checkoutId, CancellationToken ct = default)
    {
        var checkout = await sumUp.GetCheckoutAsync(checkoutId, ct);
        if (checkout is null) return PaymentSyncResult.Unknown;

        var reference = BaseReference(checkout.CheckoutReference);
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Reference == reference, ct);
        if (order is null)
        {
            logger.LogWarning("SumUp checkout {CheckoutId} has reference {Reference}, which matches no order", checkoutId, checkout.CheckoutReference);
            return PaymentSyncResult.Unknown;
        }

        if (checkout.IsPaid)
        {
            if (order.Status == OrderStatus.Cancelled)
            {
                logger.LogError("Order {Reference} was cancelled but SumUp checkout {CheckoutId} has been paid. Refund it or reinstate the order.",
                    order.Reference, checkout.Id);
                return PaymentSyncResult.AlreadyPaid;
            }

            if (!Payable.Contains(order.Status)) return PaymentSyncResult.AlreadyPaid;

            if (checkout.Amount != order.Total ||
                !string.Equals(checkout.Currency, sumUpOptions.Value.Currency, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Order {Reference}: SumUp reports {Amount} {Currency} paid, but the order total is {Total}. Not marking as paid.",
                    order.Reference, checkout.Amount, checkout.Currency, order.Total);
                return PaymentSyncResult.AmountMismatch;
            }

            var paidAt = clock.GetUtcNow().UtcDateTime;
            var transactionCode = checkout.TransactionCode ?? checkout.Transactions?.FirstOrDefault()?.TransactionCode;

            // A conditional update, so the webhook and the customer's return page can't both "win" and send two emails.
            var updated = await db.Orders
                .Where(o => o.Id == order.Id && Payable.Contains(o.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatus.Paid)
                    .SetProperty(o => o.PaidAtUtc, paidAt)
                    .SetProperty(o => o.SumUpCheckoutId, checkout.Id)
                    .SetProperty(o => o.SumUpTransactionCode, transactionCode), ct);

            if (updated == 0) return PaymentSyncResult.AlreadyPaid;

            order.Status = OrderStatus.Paid;
            order.PaidAtUtc = paidAt;
            logger.LogInformation("Order {Reference} paid ({Total} SEK)", order.Reference, order.Total);
            await notifications.OrderPaidAsync(order, ct);
            return PaymentSyncResult.MarkedPaid;
        }

        // Only the latest checkout may mark an order failed; an old, expired attempt must not override a newer one.
        if (checkout.IsFailed && order.Status == OrderStatus.AwaitingPayment && order.SumUpCheckoutId == checkout.Id)
        {
            order.Status = OrderStatus.PaymentFailed;
            await db.SaveChangesAsync(ct);
            return PaymentSyncResult.MarkedFailed;
        }

        return order.Status == OrderStatus.Paid ? PaymentSyncResult.AlreadyPaid : PaymentSyncResult.StillPending;
    }

    internal static string BaseReference(string checkoutReference)
    {
        // "IPB-261031-7KQ2-3" -> "IPB-261031-7KQ2"
        var parts = checkoutReference.Split('-');
        return parts.Length > 3 ? string.Join('-', parts.Take(3)) : checkoutReference;
    }

    private static string NewReference(DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O or 1/I, easier to read out on the phone
        var suffix = string.Create(4, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
        return $"IPB-{now:yyMMdd}-{suffix}";
    }
}
