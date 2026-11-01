using System.Globalization;

namespace IPBlueprint.Store.Shop;

/// <summary>Bound from the "Shop" configuration section.</summary>
public class ShopOptions
{
    public const string Section = "Shop";

    public string Name { get; set; } = "IP Blueprint Solutions";

    /// <summary>Public address of the site, e.g. "https://www.example.se". Used to build the SumUp webhook and return URLs.
    /// If empty, the URL of the current request is used.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Address shown to customers for questions.</summary>
    public string ContactEmail { get; set; } = "hello@example.se";

    /// <summary>Where notifications about new orders and custom requests are sent. Comma-separated.</summary>
    public string NotificationEmails { get; set; } = "";

    public decimal ShippingFlatRate { get; set; } = 69m;

    /// <summary>Orders at or above this subtotal ship for free. Set to 0 to disable free shipping.</summary>
    public decimal FreeShippingFrom { get; set; } = 600m;

    /// <summary>Most of each item a customer can put in the cart.</summary>
    public int MaxQuantityPerItem { get; set; } = 10;

    public IEnumerable<string> NotificationRecipients =>
        NotificationEmails.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Bound from the "Storage" section. Everything the shop writes (database, photos, uploads) lives under one folder,
/// so backing up the shop means copying that one folder.</summary>
public class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Absolute or content-root-relative path. Defaults to "App_Data".</summary>
    public string DataPath { get; set; } = "App_Data";
}

public sealed class StoragePaths
{
    public StoragePaths(string root)
    {
        Root = root;
        ProductImages = Path.Combine(root, "images", "products");
        GalleryImages = Path.Combine(root, "images", "gallery");
        RequestAttachments = Path.Combine(root, "requests");
        DatabaseFile = Path.Combine(root, "store.db");

        Directory.CreateDirectory(ProductImages);
        Directory.CreateDirectory(GalleryImages);
        Directory.CreateDirectory(RequestAttachments);
    }

    public string Root { get; }
    public string ProductImages { get; }
    public string GalleryImages { get; }
    public string RequestAttachments { get; }
    public string DatabaseFile { get; }
}

public static class Money
{
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    /// <summary>Formats an amount the Swedish way: "349 kr" or "349,50 kr".</summary>
    public static string Format(decimal amount) =>
        amount == decimal.Truncate(amount)
            ? amount.ToString("#,0", Swedish) + " kr"
            : amount.ToString("#,0.00", Swedish) + " kr";
}
