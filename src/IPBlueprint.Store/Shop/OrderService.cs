using System.Security.Cryptography;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Localization;
using IPBlueprint.Store.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Shop;

/// <summary>Who ordered and where it goes. The address is ignored (and may be empty) for pickup orders.</summary>
public sealed record CustomerDetails(
    string Name,
    string Email,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    string? City,
    string? Note);

/// <summary>An order took more than was in stock (two people bought the last one at the same time). The owners are told.</summary>
public sealed record StockShortage(string ProductName, int Ordered, int InStock);

/// <summary>What the admin can do next with an order in each status.</summary>
public static class OrderStatusFlow
{
    /// <summary>Statuses where the customer has paid and nothing has been refunded.</summary>
    public static readonly OrderStatus[] PaidStatuses =
        [OrderStatus.Paid, OrderStatus.InProduction, OrderStatus.Shipped, OrderStatus.ReadyForPickup, OrderStatus.Delivered];

    /// <summary>Paid but not yet sent: what the shop still has to make or pack.</summary>
    public static readonly OrderStatus[] ToDo = [OrderStatus.Paid, OrderStatus.InProduction];

    /// <summary>Pickup orders go Paid, InProduction, ReadyForPickup, Delivered (picked up); shipped orders go through
    /// Shipped instead. Each can skip production.</summary>
    public static IReadOnlyList<OrderStatus> NextSteps(OrderStatus from, DeliveryMethod delivery)
    {
        var handOver = delivery == DeliveryMethod.Pickup ? OrderStatus.ReadyForPickup : OrderStatus.Shipped;
        return from switch
        {
            OrderStatus.Paid => [OrderStatus.InProduction, handOver, OrderStatus.Refunded],
            OrderStatus.InProduction => [handOver, OrderStatus.Refunded],
            OrderStatus.Shipped or OrderStatus.ReadyForPickup => [OrderStatus.Delivered, OrderStatus.Refunded],
            OrderStatus.Delivered => [OrderStatus.Refunded],
            OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed => [OrderStatus.Cancelled],
            _ => [],
        };
    }

    public static bool CanChange(OrderStatus from, OrderStatus to, DeliveryMethod delivery) => NextSteps(from, delivery).Contains(to);

    /// <summary>The status as the admin pages show it (they're in English). Delivered reads "Picked up" for pickup orders.</summary>
    public static string AdminLabel(OrderStatus status, DeliveryMethod delivery = DeliveryMethod.Shipping) => status switch
    {
        OrderStatus.AwaitingPayment => "Awaiting payment",
        OrderStatus.PaymentFailed => "Payment failed",
        OrderStatus.InProduction => "In production",
        OrderStatus.ReadyForPickup => "Ready for pickup",
        OrderStatus.Delivered when delivery == DeliveryMethod.Pickup => "Picked up",
        _ => status.ToString(),
    };

    /// <summary>Cancelling an unpaid order is nothing the customer needs to hear about; every other change is.</summary>
    public static bool EmailsCustomer(OrderStatus to) => to != OrderStatus.Cancelled;
}

public sealed record StatusChangeRequest(OrderStatus To, string? TrackingNumber = null, bool EmailCustomer = true, bool Restock = false);

public enum StatusChangeResult
{
    Changed,
    NotFound,
    /// <summary>The order isn't in a status that allows this step (or someone else just changed it).</summary>
    NotAllowed,
}

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
    StockAlerts stockAlerts,
    TimeProvider clock,
    ILogger<OrderService> logger)
{
    private static readonly OrderStatus[] Payable = [OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed];

    public async Task<Order> CreateOrderAsync(CartSummary cart, CustomerDetails customer, CancellationToken ct = default)
    {
        if (cart.IsEmpty) throw new InvalidOperationException("The cart is empty.");

        var pickup = cart.Delivery == DeliveryMethod.Pickup;
        var order = new Order
        {
            Reference = NewReference(clock.GetUtcNow()),
            CustomerName = customer.Name.Trim(),
            Email = customer.Email.Trim(),
            Phone = customer.Phone?.Trim(),
            Delivery = cart.Delivery,
            // Nothing to send, so don't keep an address we don't need.
            AddressLine1 = pickup ? null : customer.AddressLine1?.Trim(),
            AddressLine2 = pickup ? null : customer.AddressLine2?.Trim(),
            PostalCode = pickup ? null : customer.PostalCode?.Trim(),
            City = pickup ? null : customer.City?.Trim(),
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

    /// <summary>
    /// Creates a SumUp hosted checkout for the order and returns the URL to send the customer to, or null when the order
    /// was paid or cancelled in the meantime (for example in another tab), so there is nothing left to pay.
    /// </summary>
    public async Task<string?> StartPaymentAsync(Order order, Uri siteBaseUrl, CancellationToken ct = default)
    {
        if (!Payable.Contains(order.Status))
            throw new InvalidOperationException($"Order {order.Reference} cannot be paid in status {order.Status}.");

        // Count the attempt before calling SumUp, so a call that timed out after SumUp created the checkout doesn't
        // make every later attempt reuse the same checkout reference (which SumUp refuses).
        int attempt;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var counted = await db.Orders.Where(o => o.Id == order.Id && Payable.Contains(o.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentAttempts, o => o.PaymentAttempts + 1), ct);
            if (counted == 0) return null;
            attempt = await db.Orders.Where(o => o.Id == order.Id).Select(o => o.PaymentAttempts).SingleAsync(ct);
            await tx.CommitAsync(ct);
        }

        // SumUp needs a unique reference per checkout, so retries get a suffix: IPB-261031-7KQ2, IPB-261031-7KQ2-2, ...
        var checkoutReference = attempt == 1 ? order.Reference : $"{order.Reference}-{attempt}";

        var checkout = await sumUp.CreateCheckoutAsync(new CreateCheckoutRequest(
            CheckoutReference: checkoutReference,
            Amount: order.Total,
            Currency: sumUpOptions.Value.Currency,
            Description: (order.Language == Languages.English ? "Order " : "Beställning ") + order.Reference,
            RedirectUrl: new Uri(siteBaseUrl, $"order/{order.Id}"),
            WebhookUrl: new Uri(siteBaseUrl, "webhooks/sumup")), ct);

        // Only while the order is still unpaid: if the customer paid an earlier checkout while this one was being
        // created, pointing the order at the new checkout would hide a second payment of it.
        var saved = await db.Orders.Where(o => o.Id == order.Id && Payable.Contains(o.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.SumUpCheckoutId, checkout.Id)
                .SetProperty(o => o.Status, OrderStatus.AwaitingPayment), ct);
        if (saved == 0)
        {
            logger.LogInformation("Order {Reference} was paid or cancelled while checkout {CheckoutId} was created; not using it",
                order.Reference, checkout.Id);
            return null;
        }

        // Saved above; bring the caller's copy up to date without marking it as changed.
        var entry = db.Entry(order);
        order.PaymentAttempts = attempt;
        order.SumUpCheckoutId = checkout.Id;
        order.Status = OrderStatus.AwaitingPayment;
        if (entry.State != EntityState.Detached) entry.State = EntityState.Unchanged;

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
                await ReportExtraPaymentAsync(order, checkout, cancelled: true, ct);
                return PaymentSyncResult.AlreadyPaid;
            }

            if (!Payable.Contains(order.Status))
            {
                // A second checkout for the same order (the "Pay now" retry) was paid too: the customer paid twice.
                if (checkout.Id != order.SumUpCheckoutId) await ReportExtraPaymentAsync(order, checkout, cancelled: false, ct);
                return PaymentSyncResult.AlreadyPaid;
            }

            if (checkout.Amount != order.Total ||
                !string.Equals(checkout.Currency, sumUpOptions.Value.Currency, StringComparison.OrdinalIgnoreCase))
            {
                await ReportAmountMismatchAsync(order, checkout, ct);
                return PaymentSyncResult.AmountMismatch;
            }

            var paidAt = clock.GetUtcNow().UtcDateTime;
            var transactionCode = checkout.TransactionCode ?? checkout.Transactions?.FirstOrDefault()?.TransactionCode;

            // A conditional update, so the webhook and the customer's return page can't both "win" and send two emails
            // or take the items out of stock twice. The stock change is in the same transaction, so it happens exactly
            // once together with the status change.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.Orders
                .Where(o => o.Id == order.Id && Payable.Contains(o.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatus.Paid)
                    .SetProperty(o => o.PaidAtUtc, paidAt)
                    .SetProperty(o => o.SumUpCheckoutId, checkout.Id)
                    .SetProperty(o => o.SumUpTransactionCode, transactionCode), ct);

            if (updated == 0)
            {
                await tx.RollbackAsync(ct);
                return PaymentSyncResult.AlreadyPaid;
            }

            var shortages = await TakeFromStockAsync(order, ct);
            db.OrderStatusChanges.Add(new OrderStatusChange { OrderId = order.Id, Status = OrderStatus.Paid, AtUtc = paidAt, CustomerEmailed = true });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Already saved above; stop tracking so the in-memory copy can be brought up to date without a second save.
            db.Entry(order).State = EntityState.Detached;
            order.Status = OrderStatus.Paid;
            order.PaidAtUtc = paidAt;
            order.SumUpCheckoutId = checkout.Id;
            order.SumUpTransactionCode = transactionCode;

            logger.LogInformation("Order {Reference} paid ({Total} SEK)", order.Reference, order.Total);
            foreach (var s in shortages)
                logger.LogWarning("Order {Reference} took {Ordered} × {Product}, but only {InStock} were in stock", order.Reference, s.Ordered, s.ProductName, s.InStock);

            // The payment is committed: from here on the visitor leaving must not stop the emails.
            await notifications.OrderPaidAsync(order, shortages);
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

    /// <summary>
    /// Moves a paid order along (in production, shipped, delivered, refunded) or cancels an unpaid one, and emails the
    /// customer about it. Only steps from <see cref="OrderStatusFlow"/> are allowed, and the change is conditional on
    /// the status the order had, so two admins clicking at once can't send the customer two emails.
    /// </summary>
    public async Task<StatusChangeResult> ChangeStatusAsync(Guid orderId, StatusChangeRequest request, CancellationToken ct = default)
    {
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return StatusChangeResult.NotFound;

        var from = order.Status;
        if (!OrderStatusFlow.CanChange(from, request.To, order.Delivery)) return StatusChangeResult.NotAllowed;

        var now = clock.GetUtcNow().UtcDateTime;
        var tracking = string.IsNullOrWhiteSpace(request.TrackingNumber) ? null : request.TrackingNumber.Trim();
        var emailCustomer = request.EmailCustomer && OrderStatusFlow.EmailsCustomer(request.To);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = request.To == OrderStatus.Shipped
            ? await db.Orders.Where(o => o.Id == orderId && o.Status == from).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, request.To)
                .SetProperty(o => o.ShippedAtUtc, now)
                .SetProperty(o => o.TrackingNumber, tracking), ct)
            : await db.Orders.Where(o => o.Id == orderId && o.Status == from).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, request.To), ct);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return StatusChangeResult.NotAllowed;
        }

        var backInStock = new List<int>();
        if (request.To == OrderStatus.Refunded && request.Restock && !order.StockRestored)
        {
            backInStock = await ReturnToStockAsync(order, ct);
            await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.StockRestored, true), ct);
        }

        var historyEntry = new OrderStatusChange { OrderId = orderId, Status = request.To, AtUtc = now, CustomerEmailed = emailCustomer };
        db.OrderStatusChanges.Add(historyEntry);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        db.Entry(order).State = EntityState.Detached; // saved above; just bring the copy used for the email up to date
        order.Status = request.To;
        if (request.To == OrderStatus.Shipped)
        {
            order.ShippedAtUtc = now;
            order.TrackingNumber = tracking;
        }

        logger.LogInformation("Order {Reference}: {From} -> {To}", order.Reference, from, request.To);
        // The change is committed: from here on the admin leaving the page must not stop the emails.
        if (emailCustomer && !await notifications.StatusChangedAsync(order))
        {
            await db.OrderStatusChanges.Where(c => c.Id == historyEntry.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CustomerEmailed, false), CancellationToken.None);
        }
        foreach (var productId in backInStock) await stockAlerts.NotifyIfAvailableAsync(productId, CancellationToken.None);

        return StatusChangeResult.Changed;
    }

    /// <summary>
    /// The customer paid a checkout the order didn't need. Logs an error and emails the owners, once per checkout even
    /// though SumUp and the return page may report it many times.
    /// </summary>
    private async Task ReportExtraPaymentAsync(Order order, SumUpCheckout checkout, bool cancelled, CancellationToken ct)
    {
        var first = await db.Orders
            .Where(o => o.Id == order.Id && (o.ExtraPaymentCheckoutId == null || o.ExtraPaymentCheckoutId != checkout.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExtraPaymentCheckoutId, checkout.Id), ct);
        if (first == 0) return;

        logger.LogError(cancelled
                ? "Order {Reference} was cancelled but SumUp checkout {CheckoutId} has been paid. Refund it or reinstate the order."
                : "Order {Reference} was already paid, but SumUp checkout {CheckoutId} was paid as well. Refund that checkout.",
            order.Reference, checkout.Id);
        await notifications.ExtraPaymentAsync(order, checkout, cancelled);
    }

    /// <summary>
    /// SumUp says a checkout for the order was paid, but not with the order's amount or currency, so the order was not
    /// marked paid. The customer has still been charged: logs an error and emails the owners, once per checkout. The
    /// checkout is kept in <see cref="Order.ExtraPaymentCheckoutId"/>, which also stops the customer paying again.
    /// </summary>
    private async Task ReportAmountMismatchAsync(Order order, SumUpCheckout checkout, CancellationToken ct)
    {
        var first = await db.Orders
            .Where(o => o.Id == order.Id && (o.ExtraPaymentCheckoutId == null || o.ExtraPaymentCheckoutId != checkout.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExtraPaymentCheckoutId, checkout.Id), ct);
        if (first == 0) return;

        logger.LogError("Order {Reference}: SumUp reports {Amount} {Currency} paid on checkout {CheckoutId}, but the order total is {Total}. Not marking as paid.",
            order.Reference, checkout.Amount, checkout.Currency, checkout.Id, order.Total);
        await notifications.AmountMismatchAsync(order, checkout);
    }

    /// <summary>Takes a paid order's items out of stock. Made-to-order products have no stock and are skipped.
    /// Never goes below zero; if there wasn't enough, that's returned so the owners can sort it out.</summary>
    private async Task<List<StockShortage>> TakeFromStockAsync(Order order, CancellationToken ct)
    {
        var shortages = new List<StockShortage>();
        foreach (var line in order.Lines.Where(l => l.ProductId is not null).GroupBy(l => l.ProductId!.Value))
        {
            var productId = line.Key;
            var quantity = line.Sum(l => l.Quantity);
            var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.Stock).FirstOrDefaultAsync(ct);
            if (stock is null) continue; // made to order, or the product has been deleted

            if (stock < quantity) shortages.Add(new StockShortage(line.First().ProductName, quantity, Math.Max(0, stock.Value)));

            await db.Products.Where(p => p.Id == productId && p.Stock != null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock > quantity ? p.Stock - quantity : 0), ct);

            // Remember what was really taken (saved with the order's payment), so a refund can't put back more.
            var taken = Math.Clamp(stock.Value, 0, quantity);
            foreach (var orderLine in line)
            {
                orderLine.StockTaken = Math.Min(orderLine.Quantity, taken);
                taken -= orderLine.StockTaken;
            }
        }
        return shortages;
    }

    /// <summary>Puts back what a refunded order took out of stock (not more, if stock ran short or the product was made
    /// to order then). Returns the products that were sold out and now aren't.</summary>
    private async Task<List<int>> ReturnToStockAsync(Order order, CancellationToken ct)
    {
        var backInStock = new List<int>();
        foreach (var line in order.Lines.Where(l => l.ProductId is not null && l.StockTaken > 0).GroupBy(l => l.ProductId!.Value))
        {
            var productId = line.Key;
            var quantity = line.Sum(l => l.StockTaken);
            var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.Stock).FirstOrDefaultAsync(ct);
            if (stock is null) continue; // made to order now: nothing to count

            await db.Products.Where(p => p.Id == productId && p.Stock != null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => (p.Stock > 0 ? p.Stock : 0) + quantity), ct);
            if (stock <= 0) backInStock.Add(productId);
        }
        return backInStock;
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
