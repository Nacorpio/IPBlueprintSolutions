using System.Text;
using System.Text.Encodings.Web;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Email;
using IPBlueprint.Store.Localization;
using Microsoft.Extensions.Options;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Sends the shop's emails. A failure to send is logged but never fails the order or request itself.
/// Customers get their email in the language they used on the site (stored on the order or request), since these
/// often go out from the SumUp webhook or the admin page rather than from the customer's own request.
/// Emails to the shop owners are always in Swedish.
/// </summary>
public sealed class OrderNotifications(IEmailSender email, IOptions<ShopOptions> shopOptions, ILogger<OrderNotifications> logger)
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;
    private ShopOptions Shop => shopOptions.Value;

    public async Task OrderPaidAsync(Order order, CancellationToken ct = default)
    {
        var en = IsEnglish(order.Language);

        await TrySendAsync(order.Email,
            en ? $"Thank you for your order {order.Reference}" : $"Tack för din beställning {order.Reference}",
            en
                ? $"""
                  <p>Hi {Html.Encode(order.CustomerName)},</p>
                  <p>Thank you for your order! Your payment has come through, and we'll print and pack your items as soon as we can.
                  We'll email you again when the parcel is on its way.</p>
                  {OrderSummaryHtml(order, en)}
                  <p>Questions? Just reply to this email.</p>
                  {FollowUsHtml(en)}
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej {Html.Encode(order.CustomerName)}!</p>
                  <p>Tack för din beställning! Betalningen har gått igenom, och vi printar och packar dina varor så snart vi kan.
                  Vi mejlar dig igen när paketet är på väg.</p>
                  {OrderSummaryHtml(order, en)}
                  <p>Har du frågor? Svara bara på det här mejlet.</p>
                  {FollowUsHtml(en)}
                  <p>{Html.Encode(Shop.Name)}</p>
                  """,
            replyTo: Shop.ContactEmail, ct);

        foreach (var owner in Shop.NotificationRecipients)
        {
            await TrySendAsync(owner, $"Ny betald beställning {order.Reference} ({Money.Format(order.Total)})",
                $"""
                <p>Ny betald beställning från {Html.Encode(order.CustomerName)} ({Html.Encode(order.Email)}).{(en ? " Kunden handlade på engelska." : "")}</p>
                {OrderSummaryHtml(order, english: false)}
                {AddressHtml(order)}
                {(string.IsNullOrWhiteSpace(order.CustomerNote) ? "" : $"<p><strong>Meddelande från kunden:</strong> {Html.Encode(order.CustomerNote)}</p>")}
                """, replyTo: order.Email, ct);
        }
    }

    public Task OrderShippedAsync(Order order, CancellationToken ct = default)
    {
        var en = IsEnglish(order.Language);
        var tracking = string.IsNullOrWhiteSpace(order.TrackingNumber)
            ? ""
            : $"<p>{(en ? "Tracking number" : "Spårningsnummer")}: <strong>{Html.Encode(order.TrackingNumber)}</strong></p>";

        return TrySendAsync(order.Email,
            en ? $"Your order {order.Reference} is on its way" : $"Din beställning {order.Reference} är på väg",
            en
                ? $"""
                  <p>Hi {Html.Encode(order.CustomerName)},</p>
                  <p>Your order {Html.Encode(order.Reference)} has been shipped.</p>
                  {tracking}
                  <p>If something is wrong when it arrives, reply to this email and we'll sort it out.</p>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej {Html.Encode(order.CustomerName)}!</p>
                  <p>Din beställning {Html.Encode(order.Reference)} har skickats.</p>
                  {tracking}
                  <p>Om något inte stämmer när paketet kommer fram, svara på det här mejlet så löser vi det.</p>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """,
            replyTo: Shop.ContactEmail, ct);
    }

    public async Task CustomRequestReceivedAsync(CustomOrderRequest request, CancellationToken ct = default)
    {
        var en = IsEnglish(request.Language);
        var description = Html.Encode(request.Description).Replace("\n", "<br>");

        await TrySendAsync(request.Email,
            en ? "We received your custom order request" : "Vi har tagit emot din förfrågan",
            en
                ? $"""
                  <p>Hi {Html.Encode(request.Name)},</p>
                  <p>Thanks for your request. We'll read it and reply with a price and an estimated delivery time,
                  usually within a few days. This is what you sent:</p>
                  <blockquote>{description}</blockquote>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej {Html.Encode(request.Name)}!</p>
                  <p>Tack för din förfrågan. Vi läser igenom den och återkommer med ett pris och en ungefärlig leveranstid,
                  oftast inom några dagar. Det här är vad du skickade:</p>
                  <blockquote>{description}</blockquote>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """,
            replyTo: Shop.ContactEmail, ct);

        foreach (var owner in Shop.NotificationRecipients)
        {
            await TrySendAsync(owner, $"Ny förfrågan om specialbeställning från {request.Name}",
                $"""
                <p><strong>{Html.Encode(request.Name)}</strong> ({Html.Encode(request.Email)}) har skickat en förfrågan om specialbeställning{(en ? " (på engelska, så svara gärna på engelska)" : "")}:</p>
                <blockquote>{description}</blockquote>
                <p>Färg: {Html.Encode(request.PreferredColour ?? "-")}<br>Budget: {Html.Encode(request.Budget ?? "-")}<br>
                Bifogad fil: {Html.Encode(request.AttachmentOriginalName ?? "ingen")}</p>
                <p>Öppna adminsidan för att se förfrågan och ladda ner eventuell fil.</p>
                """, replyTo: request.Email, ct);
        }
    }

    /// <summary>"Follow us on Instagram and Facebook", or nothing when no accounts are set.</summary>
    private string FollowUsHtml(bool english)
    {
        var links = Shop.SocialLinks.Select(s => $"<a href=\"{Html.Encode(s.Url)}\">{s.Name}</a>").ToList();
        if (links.Count == 0) return "";
        var joined = links.Count == 1 ? links[0] : string.Join(", ", links[..^1]) + (english ? " and " : " och ") + links[^1];
        return english
            ? $"<p>Follow us on {joined} to see what we're printing next.</p>"
            : $"<p>Följ oss på {joined} för att se vad vi printar härnäst.</p>";
    }

    private static bool IsEnglish(string? language) => Languages.Normalize(language) == Languages.English;

    private static string OrderSummaryHtml(Order order, bool english)
    {
        var sb = new StringBuilder("<table cellpadding=\"4\" style=\"border-collapse:collapse\">");
        foreach (var line in order.Lines)
        {
            sb.Append($"<tr><td>{line.Quantity} × {Html.Encode(line.ProductName)}</td><td align=\"right\">{Money.Format(line.LineTotal)}</td></tr>");
        }
        var shipping = order.ShippingCost == 0 ? (english ? "Free" : "Fri frakt") : Money.Format(order.ShippingCost);
        sb.Append($"<tr><td>{(english ? "Shipping" : "Frakt")}</td><td align=\"right\">{shipping}</td></tr>");
        sb.Append($"<tr><td><strong>{(english ? "Total (incl. VAT)" : "Totalt (inkl. moms)")}</strong></td><td align=\"right\"><strong>{Money.Format(order.Total)}</strong></td></tr>");
        sb.Append("</table>");
        sb.Append($"<p>{(english ? "Order number" : "Ordernummer")}: <strong>{Html.Encode(order.Reference)}</strong></p>");
        return sb.ToString();
    }

    private static string AddressHtml(Order o) =>
        $"""
        <p><strong>Leveransadress:</strong><br>
        {Html.Encode(o.CustomerName)}<br>
        {Html.Encode(o.AddressLine1)}<br>
        {(string.IsNullOrWhiteSpace(o.AddressLine2) ? "" : Html.Encode(o.AddressLine2) + "<br>")}
        {Html.Encode(o.PostalCode)} {Html.Encode(o.City)}<br>
        {(string.IsNullOrWhiteSpace(o.Phone) ? "" : "Telefon: " + Html.Encode(o.Phone))}</p>
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
