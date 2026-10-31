using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPBlueprint.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DescriptionEn",
                table: "Products",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameEn",
                table: "Products",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Orders",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "sv");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "CustomOrderRequests",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "sv");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DescriptionEn",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NameEn",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "CustomOrderRequests");
        }
    }
}
