using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence.Configurations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.ScopedReservationMigrations;

/// <summary>
/// Transactional schema-only reservation surface for LOCAL synthetic
/// comparison. No migrations of existing Orders/Fills or legacy indexes.
/// </summary>
[DbContext(typeof(ScopedReservationsDbContext))]
[Migration("20261023120000_AddSyntheticScopedIdempotencyReservations")]
public partial class AddSyntheticScopedIdempotencyReservations : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.CreateTable(
            name: "SyntheticScopedOrderReservations",
            columns: table => new
            {
                ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AccountKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ClientOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                LocalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                BrokerOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                InstrumentKey = table.Column<string>(type: "character varying(192)", maxLength: 192, nullable: false),
                Side = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                OrderType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                RequestedQuantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                Price = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                Classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScopedOrders",
                    x => new { x.ProviderKey, x.AccountKey, x.ClientOrderId });

                table.UniqueConstraint("AK_ScopedOrders_ExecutionOwner",
                    x => new { x.ProviderKey, x.AccountKey, x.ClientOrderId,
                        x.LocalOrderId, x.BrokerOrderId });

                table.CheckConstraint("CK_ScopedOrders_SyntheticOnly",
                    "\"Classification\" = 'SyntheticOnly'");
                table.CheckConstraint("CK_ScopedOrders_ProviderInstrument",
                    SyntheticReservationChecks.OrderScope);
                table.CheckConstraint("CK_ScopedOrders_Identity",
                    SyntheticReservationChecks.OrderText);
                table.CheckConstraint("CK_ScopedOrders_Terms",
                    SyntheticReservationChecks.OrderTerms);
            });

        m.CreateIndex(
            name: "UX_ScopedOrders_LocalOrderId",
            table: "SyntheticScopedOrderReservations",
            column: "LocalOrderId",
            unique: true);
        m.CreateIndex(
            name: "UX_ScopedOrders_ProviderAccountBroker",
            table: "SyntheticScopedOrderReservations",
            columns: new[] { "ProviderKey", "AccountKey", "BrokerOrderId" },
            unique: true);

        m.CreateTable(
            name: "SyntheticScopedExecutionReservations",
            columns: table => new
            {
                ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AccountKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ExecutionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ClientOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                LocalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                BrokerOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                Price = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                Fee = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                FeeCurrency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                Classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScopedExecutions",
                    x => new { x.ProviderKey, x.AccountKey, x.ExecutionId });

                table.ForeignKey(
                    name: "FK_ScopedExecutions_ExactOrderBinding",
                    columns: x => new { x.ProviderKey, x.AccountKey, x.ClientOrderId,
                        x.LocalOrderId, x.BrokerOrderId },
                    principalTable: "SyntheticScopedOrderReservations",
                    principalColumns: new[] { "ProviderKey", "AccountKey", "ClientOrderId",
                        "LocalOrderId", "BrokerOrderId" },
                    onDelete: ReferentialAction.Restrict);

                table.CheckConstraint("CK_ScopedExecutions_SyntheticOnly",
                    "\"Classification\" = 'SyntheticOnly'");
                table.CheckConstraint("CK_ScopedExecutions_Identity",
                    SyntheticReservationChecks.ExecutionScope);
                table.CheckConstraint("CK_ScopedExecutions_Terms",
                    SyntheticReservationChecks.ExecutionTerms);
            });

        m.CreateIndex(
            name: "IX_ScopedExecutions_ExactOrderBinding",
            table: "SyntheticScopedExecutionReservations",
            columns: new[] { "ProviderKey", "AccountKey", "ClientOrderId",
                "LocalOrderId", "BrokerOrderId" });
    }

    protected override void Down(MigrationBuilder m)
    {
        m.DropTable("SyntheticScopedExecutionReservations");
        m.DropTable("SyntheticScopedOrderReservations");
    }
}
