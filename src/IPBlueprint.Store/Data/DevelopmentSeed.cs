using Microsoft.EntityFrameworkCore;

namespace IPBlueprint.Store.Data;

/// <summary>Adds a few example products when running locally with an empty database. Never runs in production.</summary>
public static class DevelopmentSeed
{
    public static async Task RunAsync(StoreDbContext db)
    {
        if (await db.Products.AnyAsync()) return;

        var home = new Category
        {
            Slug = "hem-och-inredning", Name = "Hem & inredning", NameEn = "Home & decor", SortOrder = 1,
            Description = "Krukor, hållare och annat för hemmet.", DescriptionEn = "Planters, holders and other things for the home."
        };
        var figures = new Category { Slug = "figurer", Name = "Figurer", NameEn = "Figures", SortOrder = 2 };
        db.Categories.AddRange(home, figures);

        db.Products.AddRange(
            new Product
            {
                Slug = "geometrisk-kruka", Name = "Geometrisk kruka", Price = 249m, SortOrder = 1,
                Description = "En kantig kruka för små suckulenter. Printad i matt PLA, med dräneringshål och ett matchande fat.",
                NameEn = "Geometric planter",
                DescriptionEn = "A faceted planter for small succulents. Printed in matte PLA with a drainage hole and a matching tray.",
                Stock = 2, Material = PrintMaterial.PLA, Colour = "Vit", ColourEn = "White", Categories = [home],
            },
            new Product
            {
                Slug = "ledad-drake", Name = "Ledad drake", Price = 349m, SortOrder = 2,
                Description = "En drake med rörliga leder som printas i ett enda stycke, så att den går att posera. Cirka 30 cm lång.",
                NameEn = "Articulated dragon",
                DescriptionEn = "A poseable dragon printed in one piece, with joints that move. About 30 cm long.",
                Material = PrintMaterial.PLA, Colour = "Grön", ColourEn = "Green", Categories = [figures],
            },
            new Product
            {
                Slug = "kabelhallare-6-pack", Name = "Kabelhållare, 6-pack", Price = 129m, SortOrder = 3,
                Description = "Sex klämmor till skrivbordet som håller laddsladdarna på plats. De fästs med tejp, som ingår.",
                NameEn = "Cable organiser set",
                DescriptionEn = "Six desk clips that hold charging cables in place. They stick on with adhesive tape, which is included.",
                Stock = 0, Material = PrintMaterial.PETG, Colour = "Svart", ColourEn = "Black", Categories = [home],
            });

        await db.SaveChangesAsync();
    }
}
