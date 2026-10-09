using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001070000_AddRiskEvents")]
public partial class AddRiskEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RiskEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                EventKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                LocalSide = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                LocalQuantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                ExchangeSide = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                ExchangeQuantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RiskEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RiskEvents_EventType_EventKey",
            table: "RiskEvents",
            columns: new[] { "EventType", "EventKey" },
            unique: true,
            filter: "\"ResolvedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_RiskEvents_LastObservedAt",
            table: "RiskEvents",
            column: "LastObservedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RiskEvents");
    }
}
