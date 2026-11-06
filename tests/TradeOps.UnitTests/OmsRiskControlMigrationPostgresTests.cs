using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live = TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using FrozenClock = TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;
using Inputs = TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;

public sealed class OmsRiskControlMigrationPostgresTests
{
    [Fact]
    public async Task UpgradePreservesV1ReservationsButCannotManufactureControlStampForThem()
    {
        await using var fixture = await OmsPostgresTestDatabase.CreateAsync(PostgresPreparation.FreshMigrations);
        await using var db = new TradeOpsDbContext(new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync("20261106120000_AddOmsRiskReservationsV1");
        var command = DeribitCommandReceiptFixtures.Command() with { Scope = Scope() };
        await new OmsPostgresJournal(fixture.Source).PersistAsync(command);
        // Old v1 fixture row: upgrade must preserve bytes without inventing a control approval.
        await using (var q = fixture.Source.CreateCommand("""
INSERT INTO oms_risk_reservations_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,run_id,
 generation,schema_version,command_sha256,state_sha256,evidence_before,evidence_after,reserved_usd,margin_usd,inputs_json,inputs_sha256)
SELECT tenant_id,venue_id,environment,account_id,subaccount_id,command_id,@run,1,1,payload_sha256,
 decode(repeat('01',32),'hex'),0,1,1,1,'{}',decode(repeat('02',32),'hex') FROM oms_commands WHERE command_id=@command
"""))
        { q.Parameters.AddWithValue("run", Guid.NewGuid()); q.Parameters.AddWithValue("command", command.CommandId.Value); await q.ExecuteNonQueryAsync(); }
        await db.Database.MigrateAsync();
        Assert.False(await new OmsPostgresRiskControls(fixture.Source).IsReservationControlCurrentAsync(command.Scope, command.CommandId));
        Assert.Equal(OmsJournalWriteResult.Duplicate, await new OmsPostgresJournal(fixture.Source).PersistAsync(command));
        await using var check = fixture.Source.CreateCommand("SELECT reserved_usd,encode(state_sha256,'hex'),(SELECT count(*) FROM oms_risk_admission_controls_v1) FROM oms_risk_reservations_v1");
        await using var r = await check.ExecuteReaderAsync(); Assert.True(await r.ReadAsync());
        Assert.Equal(1, r.GetDecimal(0)); Assert.Equal(string.Concat(Enumerable.Repeat("01", 32)), r.GetString(1)); Assert.Equal(0, r.GetInt64(2));
    }

    [Fact]
    public async Task MigrationCannotRollbackAndEraseAnAdmissionAudit()
    {
        await using var fixture = await OmsPostgresTestDatabase.CreateAsync(PostgresPreparation.FreshMigrations);
        var clock = new FrozenClock(); await using var live = await Live.Open(fixture, clock);
        var (token, doc, qualified) = await live.Prepare(); var published = await live.Store.PublishAsync(token, doc, qualified);
        var command = DeribitCommandReceiptFixtures.Command() with { Scope = Scope() };
        Assert.Equal(OmsRiskAdmissionResult.Denied, await new OmsPostgresRiskAdmission(fixture.Source, new Inputs(clock) { Missing = true }, clock).AdmitAsync(command, published, qualified));
        await using var db = new TradeOpsDbContext(new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.MigrateAsync("20261106120000_AddOmsRiskReservationsV1"));
        await using var check = fixture.Source.CreateCommand("SELECT count(*) FROM oms_risk_admission_audit_v1");
        Assert.Equal(1L, await check.ExecuteScalarAsync());
    }
}
