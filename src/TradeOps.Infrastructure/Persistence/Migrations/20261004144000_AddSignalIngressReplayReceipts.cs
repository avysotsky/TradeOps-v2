using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261004144000_AddSignalIngressReplayReceipts")]
public partial class AddSignalIngressReplayReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SignalIngressReplayReceipts",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestTimestamp = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SignalIngressReplayReceipts", x => x.RequestId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SignalIngressReplayReceipts_ExpiresAt",
            table: "SignalIngressReplayReceipts",
            column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SignalIngressReplayReceipts");
    }
}
