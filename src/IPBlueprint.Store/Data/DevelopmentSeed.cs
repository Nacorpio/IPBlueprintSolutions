using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Data;

/// <summary>Adds a few example products when running locally with an empty database. Never runs in production.</summary>
public static class DevelopmentSeed
{
    public static async Task RunAsync(StoreDbContext db)
    {
        if (await db.Products.AnyAsync()) return;

        db.Products.AddRange(
            new Product
            {
                Slug = "geometric-planter", Name = "Geometric planter", Price = 249m, SortOrder = 1,
                Description = "A faceted planter for small succulents. Printed in matte PLA with a drainage hole and a matching tray.",
            },
            new Product
            {
                Slug = "articulated-dragon", Name = "Articulated dragon", Price = 349m, SortOrder = 2,
                Description = "A poseable dragon printed in one piece, with joints that move. About 30 cm long.",
            },
            new Product
            {
                Slug = "cable-organiser-set", Name = "Cable organiser set", Price = 129m, SortOrder = 3,
                Description = "Six desk clips that hold charging cables in place. They stick on with adhesive tape, which is included.",
            });

        await db.SaveChangesAsync();
    }
}
