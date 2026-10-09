using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Domain.Enums;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001064500_AddPositionSnapshots")]
public partial class AddPositionSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PositionSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Side = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                Quantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                AverageEntryPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                RealizedPnL = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                MarkPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                UnrealizedPnL = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PositionSnapshots", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PositionSnapshots_Symbol_CapturedAt",
            table: "PositionSnapshots",
            columns: new[] { "Symbol", "CapturedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PositionSnapshots");
    }
}
