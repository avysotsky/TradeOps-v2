using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

/// <summary>
/// Additive, synthetic-only broker order identity boundary. No existing
/// Orders or Fills columns/indexes are modified and NO legacy backfill occurs.
/// </summary>
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261022235900_AddSyntheticBrokerOrderIdentities")]
public partial class AddSyntheticBrokerOrderIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BrokerOrderIdentities",
            columns: table => new
            {
                OrderId = table.Column<Guid>(
                    type: "uuid", nullable: false),
                ProviderKey = table.Column<string>(
                    type: "character varying(64)", maxLength: 64,
                    nullable: false),
                AccountKey = table.Column<string>(
                    type: "character varying(128)", maxLength: 128,
                    nullable: false),
                InstrumentKey = table.Column<string>(
                    type: "character varying(192)", maxLength: 192,
                    nullable: false),
                ClientOrderId = table.Column<string>(
                    type: "character varying(100)", maxLength: 100,
                    nullable: false),
                BrokerOrderId = table.Column<string>(
                    type: "character varying(100)", maxLength: 100,
                    nullable: true),
                Classification = table.Column<string>(
                    type: "character varying(32)", maxLength: 32,
                    nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_BrokerOrderIdentities", x => x.OrderId);

                table.ForeignKey(
                    name: "FK_BrokerOrderIdentities_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);

                table.CheckConstraint(
                    "CK_BrokerOrderIdentities_SyntheticOnly",
                    "\"Classification\" = 'SyntheticOnly'");

                table.CheckConstraint(
                    "CK_BrokerOrderIdentities_QualifiedSyntheticInstrument",
                    "(\"ProviderKey\" = 'Mock' " +
                    "AND left(\"InstrumentKey\", 5) = 'MOCK:' " +
                    "AND length(\"InstrumentKey\") > 5) OR " +
                    "(\"ProviderKey\" = 'IbkrOffline' " +
                    "AND left(\"InstrumentKey\", 22) = 'SYNTHETIC-IBKR-SYMBOL:' " +
                    "AND length(\"InstrumentKey\") > 22)");

                table.CheckConstraint(
                    "CK_BrokerOrderIdentities_NonBlankScope",
                    "length(btrim(\"AccountKey\")) > 0 " +
                    "AND \"AccountKey\" = btrim(\"AccountKey\") " +
                    "AND length(btrim(\"ClientOrderId\")) > 0 " +
                    "AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
                    "AND (\"BrokerOrderId\" IS NULL OR " +
                    "(length(btrim(\"BrokerOrderId\")) > 0 " +
                    "AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\")))");
            });

        migrationBuilder.CreateIndex(
            name: "UX_BrokerOrderIdentities_ProviderAccountClient",
            table: "BrokerOrderIdentities",
            columns: new[] { "ProviderKey", "AccountKey", "ClientOrderId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_BrokerOrderIdentities_ProviderAccountBroker",
            table: "BrokerOrderIdentities",
            columns: new[] { "ProviderKey", "AccountKey", "BrokerOrderId" },
            unique: true,
            filter: "\"BrokerOrderId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BrokerOrderIdentities");
    }
}
