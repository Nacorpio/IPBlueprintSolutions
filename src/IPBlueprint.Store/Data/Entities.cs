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
}

public enum OrderStatus
{
    AwaitingPayment = 0,
    Paid = 1,
    PaymentFailed = 2,
    Shipped = 3,
    Cancelled = 4,
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
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string PostalCode { get; set; }
    public required string City { get; set; }
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
    public string? TrackingNumber { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
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
