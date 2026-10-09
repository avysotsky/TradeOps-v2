using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261004155500_AddSignalIngressRequestAudits")]
public partial class AddSignalIngressRequestAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SignalIngressRequestAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestTimestamp = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true),
                Method = table.Column<string>(
                    type: "character varying(10)",
                    maxLength: 10,
                    nullable: false),
                Path = table.Column<string>(
                    type: "character varying(200)",
                    maxLength: 200,
                    nullable: false),
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
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SignalIngressRequestAudits", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SignalIngressRequestAudits_ClientOrderId",
            table: "SignalIngressRequestAudits",
            column: "ClientOrderId");

        migrationBuilder.CreateIndex(
            name: "IX_SignalIngressRequestAudits_RequestId_ReceivedAt",
            table: "SignalIngressRequestAudits",
            columns: new[] { "RequestId", "ReceivedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_SignalIngressRequestAudits_SignalId",
            table: "SignalIngressRequestAudits",
            column: "SignalId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SignalIngressRequestAudits");
    }
}
