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
public sealed class OrderNotifications(
    IEmailSender email,
    IOptions<ShopOptions> shopOptions,
    IHttpContextAccessor http,
    ILogger<OrderNotifications> logger)
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;
    private ShopOptions Shop => shopOptions.Value;

    public async Task OrderPaidAsync(Order order, IReadOnlyList<StockShortage>? shortages = null, CancellationToken ct = default)
    {
        var en = IsEnglish(order.Language);
        var shortageHtml = shortages is { Count: > 0 }
            ? "<p><strong>Obs! Lagret räckte inte:</strong> " +
              string.Join(", ", shortages.Select(s => $"{Html.Encode(s.ProductName)} (beställt {s.Ordered}, fanns {s.InStock})")) +
              ". Printa fler eller kontakta kunden.</p>"
            : "";

        await TrySendAsync(order.Email,
            en ? $"Thank you for your order {order.Reference}" : $"Tack för din beställning {order.Reference}",
            en
                ? $"""
                  <p>Hi {Html.Encode(order.CustomerName)},</p>
                  <p>Thank you for your order! Your payment has come through, and we'll print and pack your items as soon as we can.
                  We'll email you again when the parcel is on its way.</p>
                  {OrderSummaryHtml(order, en)}
                  {StatusLinkHtml(order, en)}
                  <p>Questions? Just reply to this email.</p>
                  {FollowUsHtml(en)}
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej {Html.Encode(order.CustomerName)}!</p>
                  <p>Tack för din beställning! Betalningen har gått igenom, och vi printar och packar dina varor så snart vi kan.
                  Vi mejlar dig igen när paketet är på väg.</p>
                  {OrderSummaryHtml(order, en)}
                  {StatusLinkHtml(order, en)}
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
                {shortageHtml}
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
                  {StatusLinkHtml(order, en)}
                  <p>If something is wrong when it arrives, reply to this email and we'll sort it out.</p>
                  {FollowUsHtml(en)}
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej {Html.Encode(order.CustomerName)}!</p>
                  <p>Din beställning {Html.Encode(order.Reference)} har skickats.</p>
                  {tracking}
                  {StatusLinkHtml(order, en)}
                  <p>Om något inte stämmer när paketet kommer fram, svara på det här mejlet så löser vi det.</p>
                  {FollowUsHtml(en)}
                  <p>{Html.Encode(Shop.Name)}</p>
                  """,
            replyTo: Shop.ContactEmail, ct);
    }

    /// <summary>The customer's email for a status the admin just set. Shipped reuses <see cref="OrderShippedAsync"/>.</summary>
    public Task StatusChangedAsync(Order order, CancellationToken ct = default)
    {
        if (order.Status == OrderStatus.Shipped) return OrderShippedAsync(order, ct);

        var en = IsEnglish(order.Language);
        var hi = en ? $"<p>Hi {Html.Encode(order.CustomerName)},</p>" : $"<p>Hej {Html.Encode(order.CustomerName)}!</p>";
        var reference = Html.Encode(order.Reference);

        (string Subject, string Text)? content = order.Status switch
        {
            OrderStatus.InProduction => en
                ? ($"We're printing your order {order.Reference}",
                    $"<p>We've started printing your order {reference}. Some prints take a while, so we'll email you again when the parcel is on its way.</p>")
                : ($"Nu printar vi din beställning {order.Reference}",
                    $"<p>Vi har börjat printa din beställning {reference}. En del utskrifter tar ett tag, så vi mejlar dig igen när paketet är på väg.</p>"),
            OrderStatus.Delivered => en
                ? ($"Your order {order.Reference} has been delivered",
                    $"<p>Your order {reference} should now be with you. We hope you like it! If something isn't right, reply to this email and we'll sort it out.</p>")
                : ($"Din beställning {order.Reference} har levererats",
                    $"<p>Nu bör din beställning {reference} ha kommit fram. Vi hoppas att du gillar den! Om något inte stämmer, svara på det här mejlet så löser vi det.</p>"),
            OrderStatus.Refunded => en
                ? ($"Your order {order.Reference} has been refunded",
                    $"<p>We've refunded {Money.Format(order.Total)} for your order {reference}. It usually takes a few working days before the money is back on your card.</p>")
                : ($"Din beställning {order.Reference} har återbetalats",
                    $"<p>Vi har betalat tillbaka {Money.Format(order.Total)} för din beställning {reference}. Det brukar ta några bankdagar innan pengarna syns på ditt kort.</p>"),
            _ => default((string, string)?),
        };

        if (content is not { } c) return Task.CompletedTask;

        return TrySendAsync(order.Email, c.Subject,
            $"""
            {hi}
            {c.Text}
            {StatusLinkHtml(order, en)}
            {(order.Status == OrderStatus.Refunded ? "" : FollowUsHtml(en))}
            <p>{Html.Encode(Shop.Name)}</p>
            """,
            replyTo: Shop.ContactEmail, ct);
    }

    public Task BackInStockAsync(Product product, StockAlert alert, CancellationToken ct = default)
    {
        var en = IsEnglish(alert.Language);
        var name = en && !string.IsNullOrWhiteSpace(product.NameEn) ? product.NameEn : product.Name;
        var link = SiteLink($"products/{Uri.EscapeDataString(product.Slug)}", en);
        var button = link is null ? "" : $"<p><a href=\"{Html.Encode(link)}\">{(en ? "See it in the shop" : "Se den i butiken")}</a></p>";

        return TrySendAsync(alert.Email,
            en ? $"{name} is back in stock" : $"{name} finns i lager igen",
            en
                ? $"""
                  <p>Hi,</p>
                  <p>You asked us to tell you when <strong>{Html.Encode(name)}</strong> was back. It is now, while stocks last.</p>
                  {button}
                  <p>This was a one-off email. We've deleted your address from the list.</p>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """
                : $"""
                  <p>Hej!</p>
                  <p>Du bad oss säga till när <strong>{Html.Encode(name)}</strong> fanns igen. Nu finns den, så länge lagret räcker.</p>
                  {button}
                  <p>Det här var ett engångsmejl. Vi har tagit bort din adress från listan.</p>
                  <p>{Html.Encode(Shop.Name)}</p>
                  """,
            replyTo: Shop.ContactEmail, ct);
    }

    /// <summary>The order's own page, where the customer can see where it is. The random order id is the key.</summary>
    public string? StatusUrl(Order order) => SiteLink($"order/{order.Id}", IsEnglish(order.Language));

    private string StatusLinkHtml(Order order, bool english) =>
        StatusUrl(order) is { } url
            ? $"<p>{(english ? "You can follow your order here" : "Här kan du följa din beställning")}: <a href=\"{Html.Encode(url)}\">{Html.Encode(url)}</a></p>"
            : "";

    /// <summary>A full link to a page on the site, in the customer's language. Uses the current request's address
    /// (the webhook or admin page), or Shop:PublicBaseUrl when there is no request.</summary>
    private string? SiteLink(string path, bool english)
    {
        Uri? baseUrl = http.HttpContext is { } ctx ? SiteUrls.BaseUrl(ctx.Request, Shop)
            : !string.IsNullOrWhiteSpace(Shop.PublicBaseUrl) && Uri.TryCreate(Shop.PublicBaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var configured)
                ? configured : null;
        return baseUrl is null ? null : Seo.PageUrl(baseUrl, path, english ? Languages.English : Languages.Swedish);
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
