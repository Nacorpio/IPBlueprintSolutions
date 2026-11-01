using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Xml;
using IPBlueprint.Store.Data;
using IPBlueprint.Store.Localization;
using Microsoft.AspNetCore.Http.Extensions;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// What search engines and link previews need: one address per page and language, sitemap.xml, robots.txt and
/// structured data (JSON-LD) for products.
/// <para>
/// Every page exists in Swedish at its plain address ("/gallery") and in English with "?lang=en" ("/gallery?lang=en").
/// The language cookie still decides what a visitor sees on a plain address, but search engines don't keep cookies,
/// so the two addresses are what they index, and the hreflang links tell them the two belong together.
/// </para>
/// </summary>
public static class Seo
{
    /// <summary>The image shown in link previews of pages that have no photo of their own (1200×630).</summary>
    public const string DefaultShareImage = "/img/share.png";

    /// <summary>Longest description search engines usually show.</summary>
    public const int DescriptionLength = 155;

    /// <summary>Public pages that are always there (categories and products are added from the database).</summary>
    public static readonly IReadOnlyList<string> StaticPages =
        ["/", "/gallery", "/custom", "/info/about", "/info/terms", "/info/returns", "/info/privacy"];

    /// <summary>The paths search engines should stay out of. Cart, checkout and order pages aren't listed here: they
    /// say "noindex" themselves, which only works if crawlers are allowed to read them.</summary>
    private static readonly string[] Disallowed = ["/admin", "/language/", "/webhooks/"];

    /// <summary>The page's own address, the way it's written everywhere: lower case, no trailing slash, "/" for the
    /// shop. Product slugs are always lower case, so lower-casing the whole path is safe.</summary>
    public static string CanonicalPath(HttpRequest request)
    {
        var path = request.Path.Value?.ToLowerInvariant().TrimEnd('/') ?? "";
        return path is "" or "/index" ? "/" : path;
    }

    /// <summary>The full address of <paramref name="path"/> in <paramref name="language"/>.</summary>
    public static string PageUrl(Uri baseUrl, string path, string language)
    {
        var url = new Uri(baseUrl, path.TrimStart('/')).AbsoluteUri;
        return Languages.Normalize(language) == Languages.English ? $"{url}?{Languages.QueryKey}={Languages.English}" : url;
    }

    /// <summary>The address of the current page in another language, keeping any other query string. Used by the
    /// language switch, so the English pages are plain links that crawlers can follow.</summary>
    public static string WithLanguage(HttpRequest request, string language)
    {
        var query = new QueryBuilder(request.Query
            .Where(q => !string.Equals(q.Key, Languages.QueryKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => KeyValuePair.Create(q.Key, v ?? ""))));
        query.Add(Languages.QueryKey, Languages.Normalize(language));
        return $"{request.PathBase}{request.Path}{query}";
    }

    /// <summary>Shortens a text to fit a search result, cutting at a word and adding "…" when it had to cut.</summary>
    public static string Describe(string? text, int maxLength = DescriptionLength)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= maxLength) return flat;
        var cut = flat.LastIndexOf(' ', maxLength - 1);
        return flat[..(cut > maxLength / 2 ? cut : maxLength - 1)].TrimEnd(',', '.', ';', ':', '-') + "…";
    }

    public static string RobotsTxt(Uri baseUrl)
    {
        var sb = new StringBuilder("User-agent: *\n");
        foreach (var path in Disallowed) sb.Append("Disallow: ").Append(path).Append('\n');
        sb.Append("\nSitemap: ").Append(new Uri(baseUrl, "sitemap.xml").AbsoluteUri).Append('\n');
        return sb.ToString();
    }

    /// <summary>Lists every public page in both languages, each with links to its other language.</summary>
    public static string SitemapXml(Uri baseUrl, IEnumerable<string> productSlugs, IEnumerable<string>? categorySlugs = null)
    {
        const string ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        const string xhtml = "http://www.w3.org/1999/xhtml";
        var paths = StaticPages
            .Concat((categorySlugs ?? []).Select(s => "/categories/" + Uri.EscapeDataString(s)))
            .Concat(productSlugs.Select(s => "/products/" + Uri.EscapeDataString(s)));

        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            xml.WriteStartElement("urlset", ns);
            xml.WriteAttributeString("xmlns", "xhtml", null, xhtml);
            foreach (var path in paths)
            {
                foreach (var language in new[] { Languages.Swedish, Languages.English })
                {
                    xml.WriteStartElement("url", ns);
                    xml.WriteElementString("loc", ns, PageUrl(baseUrl, path, language));
                    foreach (var (hreflang, target) in Alternates(baseUrl, path))
                    {
                        xml.WriteStartElement("xhtml", "link", xhtml);
                        xml.WriteAttributeString("rel", "alternate");
                        xml.WriteAttributeString("hreflang", hreflang);
                        xml.WriteAttributeString("href", target);
                        xml.WriteEndElement();
                    }
                    xml.WriteEndElement();
                }
            }
            xml.WriteEndElement();
        }
        // StringBuilder output says utf-16 in the declaration; the response is sent as UTF-8.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    /// <summary>The hreflang links for a page: Swedish, English, and Swedish again as the default for everyone else.</summary>
    public static IEnumerable<(string HrefLang, string Url)> Alternates(Uri baseUrl, string path)
    {
        var swedish = PageUrl(baseUrl, path, Languages.Swedish);
        yield return (Languages.Swedish, swedish);
        yield return (Languages.English, PageUrl(baseUrl, path, Languages.English));
        yield return ("x-default", swedish);
    }

    /// <summary>schema.org Product with an Offer in SEK, so search results can show the price.</summary>
    public static string ProductJsonLd(Product product, Uri baseUrl, string pageUrl, ShopOptions shop)
    {
        var shipping = shop.FreeShippingFrom > 0 && product.Price >= shop.FreeShippingFrom ? 0m : shop.ShippingFlatRate;
        var offer = new JsonObject
        {
            ["@type"] = "Offer",
            ["url"] = pageUrl,
            ["price"] = Amount(product.Price),
            ["priceCurrency"] = "SEK",
            ["availability"] = product.IsSoldOut ? "https://schema.org/OutOfStock"
                : product.IsMadeToOrder ? "https://schema.org/MadeToOrder"
                : "https://schema.org/InStock",
            ["itemCondition"] = "https://schema.org/NewCondition",
            ["seller"] = new JsonObject { ["@type"] = "Organization", ["name"] = shop.Name },
            ["shippingDetails"] = new JsonObject
            {
                ["@type"] = "OfferShippingDetails",
                ["shippingRate"] = new JsonObject { ["@type"] = "MonetaryAmount", ["value"] = Amount(shipping), ["currency"] = "SEK" },
                ["shippingDestination"] = new JsonObject { ["@type"] = "DefinedRegion", ["addressCountry"] = "SE" },
            },
        };
        var json = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Product",
            ["name"] = product.DisplayName,
            ["sku"] = product.Slug,
            ["url"] = pageUrl,
            ["brand"] = new JsonObject { ["@type"] = "Brand", ["name"] = shop.Name },
            ["offers"] = offer,
        };
        if (!string.IsNullOrWhiteSpace(product.DisplayDescription)) json["description"] = product.DisplayDescription;
        if (ProductImagePath(product) is { } image) json["image"] = new JsonArray(new Uri(baseUrl, image.TrimStart('/')).AbsoluteUri);
        return Serialize(json);
    }

    /// <summary>schema.org WebSite and the store itself, for the front page.</summary>
    public static string StoreJsonLd(Uri baseUrl, ShopOptions shop, string description)
    {
        var home = baseUrl.AbsoluteUri;
        var json = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = new JsonArray(
                new JsonObject
                {
                    ["@type"] = "WebSite",
                    ["@id"] = home + "#website",
                    ["url"] = home,
                    ["name"] = shop.Name,
                    ["inLanguage"] = new JsonArray("sv-SE", "en-GB"),
                    ["publisher"] = new JsonObject { ["@id"] = home + "#store" },
                },
                StoreNode(baseUrl, shop, description)),
        };
        return Serialize(json);
    }

    /// <summary>
    /// The store as both an online shop and a local business in <see cref="ShopOptions.Town"/>, so searches like
    /// "3D-utskrift Smedjebacken" can find it. Only the town and region are given, never a street address: pickup is
    /// arranged by email. A Google Business Profile (set up by hand) does the most for local searches; this backs it up.
    /// </summary>
    private static JsonObject StoreNode(Uri baseUrl, ShopOptions shop, string description)
    {
        var home = baseUrl.AbsoluteUri;
        var hasTown = !string.IsNullOrWhiteSpace(shop.Town);
        var node = new JsonObject
        {
            ["@type"] = hasTown ? new JsonArray("OnlineStore", "LocalBusiness") : "OnlineStore",
            ["@id"] = home + "#store",
            ["name"] = shop.Name,
            ["url"] = home,
            ["description"] = description,
            ["logo"] = new Uri(baseUrl, "img/logo-mark.png").AbsoluteUri,
            ["image"] = new Uri(baseUrl, DefaultShareImage.TrimStart('/')).AbsoluteUri,
            ["email"] = shop.ContactEmail,
            ["currenciesAccepted"] = "SEK",
            ["paymentAccepted"] = "Card",
            ["sameAs"] = new JsonArray([.. shop.SocialLinks.Select(s => (JsonNode)s.Url)]),
        };

        if (!hasTown)
        {
            node["areaServed"] = "SE";
            return node;
        }

        var address = new JsonObject { ["@type"] = "PostalAddress", ["addressLocality"] = shop.Town.Trim() };
        if (!string.IsNullOrWhiteSpace(shop.Region)) address["addressRegion"] = shop.Region.Trim();
        address["addressCountry"] = "SE";
        node["address"] = address;
        node["areaServed"] = new JsonArray(
            new JsonObject { ["@type"] = "City", ["name"] = shop.Town.Trim() },
            new JsonObject { ["@type"] = "Country", ["name"] = "Sverige" });
        node["knowsAbout"] = new JsonArray("3D-utskrift", "3D printing", "PLA", "PETG", "ABS");
        return node;
    }

    public static string? ProductImagePath(Product product) =>
        product.ImageFileName is { } file ? "/images/products/" + Uri.EscapeDataString(file) : null;

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // The default encoder writes <, > and & as < etc., so the JSON can't end the <script> block it sits in.
    private static string Serialize(JsonNode json) => json.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
