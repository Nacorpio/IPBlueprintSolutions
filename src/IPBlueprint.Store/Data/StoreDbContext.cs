using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Data;

public class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CustomOrderRequest> CustomOrderRequests => Set<CustomOrderRequest>();
    public DbSet<GalleryImage> GalleryImages => Set<GalleryImage>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<StockAlert> StockAlerts => Set<StockAlert>();
    public DbSet<OrderStatusChange> OrderStatusChanges => Set<OrderStatusChange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Slug).IsUnique();
            e.Property(p => p.Slug).HasMaxLength(80);
            e.Property(p => p.Name).HasMaxLength(120);
            e.Property(p => p.Description).HasMaxLength(4000);
            e.Property(p => p.NameEn).HasMaxLength(120);
            e.Property(p => p.DescriptionEn).HasMaxLength(4000);
            e.Property(p => p.Material).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Colour).HasMaxLength(40);
            e.Property(p => p.ColourEn).HasMaxLength(40);
            e.HasMany(p => p.Categories).WithMany(c => c.Products).UsingEntity("ProductCategory");
        });

        b.Entity<Category>(e =>
        {
            e.HasIndex(c => c.Slug).IsUnique();
            e.Property(c => c.Slug).HasMaxLength(80);
            e.Property(c => c.Name).HasMaxLength(80);
            e.Property(c => c.NameEn).HasMaxLength(80);
            e.Property(c => c.Description).HasMaxLength(1000);
            e.Property(c => c.DescriptionEn).HasMaxLength(1000);
        });

        b.Entity<StockAlert>(e =>
        {
            e.HasIndex(a => new { a.ProductId, a.Email }).IsUnique();
            e.Property(a => a.Email).HasMaxLength(200);
            e.Property(a => a.Language).HasMaxLength(5);
            e.HasOne<Product>().WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderStatusChange>(e =>
        {
            e.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(o => o.Reference).IsUnique();
            e.HasIndex(o => o.SumUpCheckoutId);
            e.Property(o => o.Reference).HasMaxLength(32);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Language).HasMaxLength(5);
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLine>().Ignore(l => l.LineTotal);

        b.Entity<CustomOrderRequest>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Description).HasMaxLength(4000);
            e.Property(r => r.Language).HasMaxLength(5);
        });

        b.Entity<GalleryImage>(e =>
        {
            e.HasIndex(g => g.SortOrder);
            e.Property(g => g.FileName).HasMaxLength(64);
            e.Property(g => g.ThumbnailFileName).HasMaxLength(64);
            e.Property(g => g.Caption).HasMaxLength(300);
            e.Property(g => g.CaptionEn).HasMaxLength(300);
        });
    }
}
