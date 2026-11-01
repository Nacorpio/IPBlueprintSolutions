namespace IPBlueprint.Store.Data;

public class Product
{
    public int Id { get; set; }

    /// <summary>URL-friendly identifier, e.g. "dragon-planter".</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = "";

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

    /// <summary>Random file name on disk (never the user's own name, to avoid path tricks).</summary>
    public string? AttachmentFileName { get; set; }

    /// <summary>The name the customer's file had, for display only.</summary>
    public string? AttachmentOriginalName { get; set; }

    public CustomRequestStatus Status { get; set; } = CustomRequestStatus.New;
    public string? AdminNotes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
