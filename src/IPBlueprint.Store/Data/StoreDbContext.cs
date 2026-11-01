using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Data;

public class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CustomOrderRequest> CustomOrderRequests => Set<CustomOrderRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Slug).IsUnique();
            e.Property(p => p.Slug).HasMaxLength(80);
            e.Property(p => p.Name).HasMaxLength(120);
            e.Property(p => p.Description).HasMaxLength(4000);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(o => o.Reference).IsUnique();
            e.HasIndex(o => o.SumUpCheckoutId);
            e.Property(o => o.Reference).HasMaxLength(32);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLine>().Ignore(l => l.LineTotal);

        b.Entity<CustomOrderRequest>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Description).HasMaxLength(4000);
        });
    }
}
