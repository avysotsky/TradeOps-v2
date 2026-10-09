using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261004171000_AddTradingViewDeliveryAudits")]
public partial class AddTradingViewDeliveryAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TradingViewDeliveryAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(
                    type: "uuid",
                    nullable: false),
                EventId = table.Column<string>(
                    type: "character varying(200)",
                    maxLength: 200,
                    nullable: true),
                ReceivedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true),
                DurationMilliseconds = table.Column<long>(
                    type: "bigint",
                    nullable: true),
                Outcome = table.Column<string>(
                    type: "character varying(30)",
                    maxLength: 30,
                    nullable: false),
                HttpStatusCode = table.Column<int>(
                    type: "integer",
                    nullable: true),
                SignalId = table.Column<Guid>(
                    type: "uuid",
                    nullable: true),
                OrderId = table.Column<Guid>(
                    type: "uuid",
                    nullable: true),
                ClientOrderId = table.Column<string>(
                    type: "character varying(100)",
                    maxLength: 100,
                    nullable: true),
                Symbol = table.Column<string>(
                    type: "character varying(50)",
                    maxLength: 50,
                    nullable: true),
                Action = table.Column<string>(
                    type: "character varying(10)",
                    maxLength: 10,
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TradingViewDeliveryAudits",
                    x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TradingViewDeliveryAudits_ClientOrderId",
            table: "TradingViewDeliveryAudits",
            column: "ClientOrderId");

        migrationBuilder.CreateIndex(
            name: "IX_TradingViewDeliveryAudits_EventId_ReceivedAt",
            table: "TradingViewDeliveryAudits",
            columns: new[] { "EventId", "ReceivedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_TradingViewDeliveryAudits_ReceivedAt",
            table: "TradingViewDeliveryAudits",
            column: "ReceivedAt");

        migrationBuilder.CreateIndex(
            name: "IX_TradingViewDeliveryAudits_SignalId",
            table: "TradingViewDeliveryAudits",
            column: "SignalId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TradingViewDeliveryAudits");
    }
}
