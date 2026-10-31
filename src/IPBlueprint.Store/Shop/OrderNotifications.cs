using System.Text;
using System.Text.Encodings.Web;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Shop;

/// <summary>Sends the shop's emails. A failure to send is logged but never fails the order or request itself.</summary>
public sealed class OrderNotifications(IEmailSender email, IOptions<ShopOptions> shopOptions, ILogger<OrderNotifications> logger)
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;
    private ShopOptions Shop => shopOptions.Value;

    public async Task OrderPaidAsync(Order order, CancellationToken ct = default)
    {
        var summary = OrderSummaryHtml(order);

        await TrySendAsync(order.Email, $"Thank you for your order {order.Reference}",
            $"""
            <p>Hi {Html.Encode(order.CustomerName)},</p>
            <p>Thank you for your order! Your payment has come through, and we'll print and pack your items as soon as we can.
            We'll email you again when the parcel is on its way.</p>
            {summary}
            <p>Questions? Just reply to this email.</p>
            <p>{Html.Encode(Shop.Name)}</p>
            """, replyTo: Shop.ContactEmail, ct);

        foreach (var owner in Shop.NotificationRecipients)
        {
            await TrySendAsync(owner, $"New paid order {order.Reference} ({Money.Format(order.Total)})",
                $"""
                <p>New paid order from {Html.Encode(order.CustomerName)} ({Html.Encode(order.Email)}).</p>
                {summary}
                {AddressHtml(order)}
                {(string.IsNullOrWhiteSpace(order.CustomerNote) ? "" : $"<p><strong>Note from customer:</strong> {Html.Encode(order.CustomerNote)}</p>")}
                """, replyTo: order.Email, ct);
        }
    }

    public Task OrderShippedAsync(Order order, CancellationToken ct = default) =>
        TrySendAsync(order.Email, $"Your order {order.Reference} is on its way",
            $"""
            <p>Hi {Html.Encode(order.CustomerName)},</p>
            <p>Your order {Html.Encode(order.Reference)} has been shipped.</p>
            {(string.IsNullOrWhiteSpace(order.TrackingNumber) ? "" : $"<p>Tracking number: <strong>{Html.Encode(order.TrackingNumber)}</strong></p>")}
            <p>If something is wrong when it arrives, reply to this email and we'll sort it out.</p>
            <p>{Html.Encode(Shop.Name)}</p>
            """, replyTo: Shop.ContactEmail, ct);

    public async Task CustomRequestReceivedAsync(CustomOrderRequest request, CancellationToken ct = default)
    {
        await TrySendAsync(request.Email, "We received your custom order request",
            $"""
            <p>Hi {Html.Encode(request.Name)},</p>
            <p>Thanks for your request. We'll read it and reply with a price and an estimated delivery time,
            usually within a few days. This is what you sent:</p>
            <blockquote>{Html.Encode(request.Description).Replace("\n", "<br>")}</blockquote>
            <p>{Html.Encode(Shop.Name)}</p>
            """, replyTo: Shop.ContactEmail, ct);

        foreach (var owner in Shop.NotificationRecipients)
        {
            await TrySendAsync(owner, $"New custom order request from {request.Name}",
                $"""
                <p><strong>{Html.Encode(request.Name)}</strong> ({Html.Encode(request.Email)}) sent a custom order request:</p>
                <blockquote>{Html.Encode(request.Description).Replace("\n", "<br>")}</blockquote>
                <p>Colour: {Html.Encode(request.PreferredColour ?? "-")}<br>Budget: {Html.Encode(request.Budget ?? "-")}<br>
                Attachment: {Html.Encode(request.AttachmentOriginalName ?? "none")}</p>
                <p>Open the admin page to see it and download any attachment.</p>
                """, replyTo: request.Email, ct);
        }
    }

    private static string OrderSummaryHtml(Order order)
    {
        var sb = new StringBuilder("<table cellpadding=\"4\" style=\"border-collapse:collapse\">");
        foreach (var line in order.Lines)
        {
            sb.Append($"<tr><td>{line.Quantity} × {Html.Encode(line.ProductName)}</td><td align=\"right\">{Money.Format(line.LineTotal)}</td></tr>");
        }
        sb.Append($"<tr><td>Shipping</td><td align=\"right\">{(order.ShippingCost == 0 ? "Free" : Money.Format(order.ShippingCost))}</td></tr>");
        sb.Append($"<tr><td><strong>Total (incl. VAT)</strong></td><td align=\"right\"><strong>{Money.Format(order.Total)}</strong></td></tr>");
        sb.Append("</table>");
        sb.Append($"<p>Order number: <strong>{Html.Encode(order.Reference)}</strong></p>");
        return sb.ToString();
    }

    private static string AddressHtml(Order o) =>
        $"""
        <p><strong>Ship to:</strong><br>
        {Html.Encode(o.CustomerName)}<br>
        {Html.Encode(o.AddressLine1)}<br>
        {(string.IsNullOrWhiteSpace(o.AddressLine2) ? "" : Html.Encode(o.AddressLine2) + "<br>")}
        {Html.Encode(o.PostalCode)} {Html.Encode(o.City)}<br>
        {(string.IsNullOrWhiteSpace(o.Phone) ? "" : "Phone: " + Html.Encode(o.Phone))}</p>
        """;

    private async Task TrySendAsync(string to, string subject, string body, string? replyTo, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(to, subject, body, replyTo, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send email '{Subject}' to {To}", subject, to);
        }
    }
}
