using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001110000_AddOperationalRunHistory")]
public partial class AddOperationalRunHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "LatestRunId",
            table: "OperationalRunStatuses",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OperationalRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RunType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                Succeeded = table.Column<bool>(type: "boolean", nullable: true),
                OrdersScanned = table.Column<int>(type: "integer", nullable: false),
                OrdersUpdated = table.Column<int>(type: "integer", nullable: false),
                OrderIssues = table.Column<int>(type: "integer", nullable: false),
                OrdersMissingOnExchange = table.Column<int>(type: "integer", nullable: false),
                PositionsCompared = table.Column<int>(type: "integer", nullable: false),
                PositionMismatches = table.Column<int>(type: "integer", nullable: false),
                PositionSnapshots = table.Column<int>(type: "integer", nullable: false),
                ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationalRuns", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OperationalRuns_RunType_StartedAt",
            table: "OperationalRuns",
            columns: new[] { "RunType", "StartedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OperationalRuns");

        migrationBuilder.DropColumn(
            name: "LatestRunId",
            table: "OperationalRunStatuses");
    }
}
