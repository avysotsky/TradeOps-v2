using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001063000_AddFills")]
public partial class AddFills : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Fills",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ExchangeFillId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                Price = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                Fee = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                FeeCurrency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                FilledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Fills", x => x.Id);
                table.ForeignKey(
                    name: "FK_Fills_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Fills_ExchangeFillId",
            table: "Fills",
            column: "ExchangeFillId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Fills_OrderId",
            table: "Fills",
            column: "OrderId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Fills");
    }
}
