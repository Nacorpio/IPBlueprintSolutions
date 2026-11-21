using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPBlueprint.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPaymentAttemptAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentIssues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CheckoutId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    FoundAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentIssues_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIssues_CheckoutId",
                table: "PaymentIssues",
                column: "CheckoutId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIssues_OrderId",
                table: "PaymentIssues",
                column: "OrderId");

            // Keep what the old single column knew: one unresolved issue per order that had a stray checkout. The amount
            // wasn't stored, so it stays unknown. An unpaid order means the amount was wrong; a cancelled one was paid
            // after cancelling; anything else was already paid, so the customer paid twice.
            migrationBuilder.Sql("""
                INSERT INTO "PaymentIssues" ("OrderId", "CheckoutId", "Kind", "FoundAtUtc")
                SELECT "Id", "ExtraPaymentCheckoutId",
                       CASE
                           WHEN "Status" IN ('AwaitingPayment', 'PaymentFailed') THEN 'WrongAmount'
                           WHEN "Status" = 'Cancelled' THEN 'PaidAfterCancel'
                           ELSE 'DoubleCharge'
                       END,
                       COALESCE("PaidAtUtc", "CreatedAtUtc")
                FROM "Orders"
                WHERE "ExtraPaymentCheckoutId" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "ExtraPaymentCheckoutId",
                table: "Orders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtraPaymentCheckoutId",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            // The old column held one checkout per order: keep the latest unresolved one.
            migrationBuilder.Sql("""
                UPDATE "Orders" SET "ExtraPaymentCheckoutId" = (
                    SELECT "CheckoutId" FROM "PaymentIssues"
                    WHERE "PaymentIssues"."OrderId" = "Orders"."Id" AND "ResolvedAtUtc" IS NULL
                    ORDER BY "FoundAtUtc" DESC LIMIT 1);
                """);

            migrationBuilder.DropTable(
                name: "PaymentIssues");

            migrationBuilder.DropColumn(
                name: "LastPaymentAttemptAtUtc",
                table: "Orders");
        }
    }
}
