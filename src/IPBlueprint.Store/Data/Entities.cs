using IPBlueprint.Store.Localization;

namespace IPBlueprint.Store.Data;

public class Product
{
    public int Id { get; set; }

    /// <summary>URL-friendly identifier, e.g. "dragon-planter".</summary>
    public required string Slug { get; set; }

    /// <summary>The Swedish name. The shop is Swedish first, so this is the one that is always filled in.</summary>
    public required string Name { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Optional English name. When empty, English visitors see the Swedish one.</summary>
    public string? NameEn { get; set; }

    /// <summary>Optional English description. When empty, English visitors see the Swedish one.</summary>
    public string? DescriptionEn { get; set; }

    /// <summary>The name in the language the visitor is browsing in.</summary>
    public string DisplayName => Languages.IsEnglish && !string.IsNullOrWhiteSpace(NameEn) ? NameEn : Name;

    /// <summary>The description in the language the visitor is browsing in.</summary>
    public string DisplayDescription => Languages.IsEnglish && !string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionEn : Description;

    /// <summary>Price in SEK, including VAT (Swedish consumer prices must include moms).</summary>
    public decimal Price { get; set; }

    /// <summary>File name of the product photo inside the product image folder.</summary>
    public string? ImageFileName { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>How many are ready to ship, or null for "made to order" (printed when someone orders, never runs out).
    /// Goes down when a payment is confirmed.</summary>
    public int? Stock { get; set; }

    public bool IsMadeToOrder => Stock is null;

    public bool IsSoldOut => Stock is <= 0;

    /// <summary>How many a customer can buy right now: the stock, or the per-item cart limit for made-to-order items.</summary>
    public int AvailableToBuy(int maxPerItem) => Stock is { } s ? Math.Clamp(s, 0, maxPerItem) : maxPerItem;

    public PrintMaterial? Material { get; set; }

    /// <summary>Optional Swedish colour name, e.g. "Svart". Used for the colour filter in the shop.</summary>
    public string? Colour { get; set; }

    /// <summary>Optional English colour name. When empty, English visitors see the Swedish one.</summary>
    public string? ColourEn { get; set; }

    public string? DisplayColour => Languages.IsEnglish && !string.IsNullOrWhiteSpace(ColourEn) ? ColourEn : Colour;

    public List<Category> Categories { get; set; } = [];
}

public enum PrintMaterial
{
    PLA = 0,
    PETG = 1,
    ABS = 2,
}

/// <summary>A group of products in the shop, e.g. "Figurer". Each one has its own page at /categories/{slug}.</summary>
public class Category
{
    public int Id { get; set; }

    public required string Slug { get; set; }

    /// <summary>The Swedish name, always filled in.</summary>
    public required string Name { get; set; }

    /// <summary>Optional English name. When empty, English visitors see the Swedish one.</summary>
    public string? NameEn { get; set; }

    /// <summary>Optional short Swedish text shown at the top of the category page (and to search engines).</summary>
    public string? Description { get; set; }

    public string? DescriptionEn { get; set; }

    public string DisplayName => Languages.IsEnglish && !string.IsNullOrWhiteSpace(NameEn) ? NameEn : Name;

    public string? DisplayDescription => Languages.IsEnglish && !string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionEn : Description;

    /// <summary>Lower numbers are shown first in the shop's category tabs.</summary>
    public int SortOrder { get; set; }

    public List<Product> Products { get; set; } = [];
}

/// <summary>Someone who asked to be emailed when a sold-out product is back. Deleted once the email has gone out.</summary>
public class StockAlert
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public required string Email { get; set; }

    /// <summary>"sv" or "en", so the email is in the language they asked in.</summary>
    public string Language { get; set; } = Languages.Swedish;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Where an order is. Paid, InProduction, Shipped and Delivered is the normal path; Refunded can follow any of them.
/// Cancelled is only for orders that were never paid. Stored as text, so the numbers don't matter.
/// </summary>
public enum OrderStatus
{
    AwaitingPayment = 0,
    Paid = 1,
    PaymentFailed = 2,
    Shipped = 3,
    Cancelled = 4,
    InProduction = 5,
    Delivered = 6,
    Refunded = 7,
    /// <summary>Pickup orders only: made and waiting for the customer to collect it. Delivered then means "picked up".</summary>
    ReadyForPickup = 8,
}

/// <summary>How the customer gets the order: by post, or by picking it up from us (free, time agreed by email or text).
/// Stored as text.</summary>
public enum DeliveryMethod
{
    Shipping = 0,
    Pickup = 1,
}

/// <summary>One step in an order's history, shown on the customer's order page and in the admin.</summary>
public class OrderStatusChange
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public bool CustomerEmailed { get; set; }
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short, human-friendly order number shown to customers, e.g. "IPB-261031-7KQ2".</summary>
    public required string Reference { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.AwaitingPayment;

    public required string CustomerName { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }

    public DeliveryMethod Delivery { get; set; } = DeliveryMethod.Shipping;

    public bool IsPickup => Delivery == DeliveryMethod.Pickup;

    /// <summary>The address fields are empty for pickup orders: there is nothing to send.</summary>
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string Country { get; set; } = "SE";
    public string? CustomerNote { get; set; }

    /// <summary>"sv" or "en": the language the customer ordered in, so their emails are written in it too.</summary>
    public string Language { get; set; } = Languages.Swedish;

    public decimal Subtotal { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal Total { get; set; }

    /// <summary>Number of SumUp checkouts created for this order (a retry after a failed payment creates a new one).</summary>
    public int PaymentAttempts { get; set; }

    /// <summary>The most recent SumUp checkout id.</summary>
    public string? SumUpCheckoutId { get; set; }

    public string? SumUpTransactionCode { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAtUtc { get; set; }
    public DateTime? ShippedAtUtc { get; set; }

    /// <summary>Optional, set when the order is marked as shipped. Shown to the customer in the email and on the order page.</summary>
    public string? TrackingNumber { get; set; }

    /// <summary>True once a refund put the items back in stock, so it can't happen twice.</summary>
    public bool StockRestored { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    public List<OrderStatusChange> History { get; set; } = [];
}

public class OrderLine
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }

    /// <summary>Kept for reference; the name and price below are snapshots so later product edits don't change old orders.</summary>
    public int? ProductId { get; set; }

    public required string ProductName { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;
}

public enum CustomRequestStatus
{
    New = 0,
    Quoted = 1,
    Accepted = 2,
    Completed = 3,
    Declined = 4,
}

public class CustomOrderRequest
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string Description { get; set; }
    public string? PreferredColour { get; set; }
    public string? Budget { get; set; }

    /// <summary>Whether the customer wants it sent or would rather pick it up from us.</summary>
    public DeliveryMethod Delivery { get; set; } = DeliveryMethod.Shipping;

    /// <summary>"sv" or "en": the language the customer wrote in, so the confirmation email matches.</summary>
    public string Language { get; set; } = Languages.Swedish;

    /// <summary>Random file name on disk (never the user's own name, to avoid path tricks).</summary>
    public string? AttachmentFileName { get; set; }

    /// <summary>The name the customer's file had, for display only.</summary>
    public string? AttachmentOriginalName { get; set; }

    public CustomRequestStatus Status { get; set; } = CustomRequestStatus.New;
    public string? AdminNotes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A photo of something we've printed, shown on the public gallery page.</summary>
public class GalleryImage
{
    public int Id { get; set; }

    /// <summary>The resized photo (WebP) inside the gallery folder. Random name, never the uploader's.</summary>
    public required string FileName { get; set; }

    /// <summary>A smaller copy (WebP) for the grid.</summary>
    public required string ThumbnailFileName { get; set; }

    /// <summary>Size of the resized photo, so the page can reserve space before it loads.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }

    /// <summary>Optional Swedish caption.</summary>
    public string? Caption { get; set; }

    /// <summary>Optional English caption. When empty, English visitors see the Swedish one.</summary>
    public string? CaptionEn { get; set; }

    /// <summary>The caption in the language the visitor is browsing in, or null if there is none.</summary>
    public string? DisplayCaption => Languages.IsEnglish && !string.IsNullOrWhiteSpace(CaptionEn) ? CaptionEn : Caption;

    /// <summary>Lower numbers are shown first.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
