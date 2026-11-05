using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPBlueprint.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockTakenAndExtraPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtraPaymentCheckoutId",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockTaken",
                table: "OrderLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Orders paid before this column existed: assume the whole quantity was taken from stocked products, which
            // is what a refund used to put back.
            migrationBuilder.Sql("""
                UPDATE OrderLines SET StockTaken = Quantity
                WHERE OrderId IN (SELECT Id FROM Orders WHERE PaidAtUtc IS NOT NULL AND StockRestored = 0)
                  AND ProductId IN (SELECT Id FROM Products WHERE Stock IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtraPaymentCheckoutId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "StockTaken",
                table: "OrderLines");
        }
    }
}
