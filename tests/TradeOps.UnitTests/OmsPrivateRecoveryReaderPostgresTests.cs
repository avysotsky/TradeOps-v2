using System.Diagnostics;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateRecoveryReaderPostgresTests
{
    [Fact]
    public async Task FreshReader_ComposesCommittedLedgerWithRecoveryProjection()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        await capture.CaptureAsync(Prior());
        await capture.CaptureAsync(Fill());
        await capture.CaptureAsync(Report(filled: 4));
        var recovery = new DeribitDurableOrderRecovery(new OmsPostgresPrivateRecoveryReader(db.Source));
        var result = await recovery.RebuildAsync(Account, Epoch, From, Through, Complete);
        Assert.True(result.Evidence.IsUsable);
        Assert.Equal(Account, result.Evidence.Scope);
        Assert.NotEqual(Guid.Empty, result.Evidence.SnapshotId);
        Assert.Equal(TimeSpan.Zero, result.Evidence.ReadAtUtc.Offset);
        Assert.Equal(3, result.Evidence.AcceptedFactCount);
        Assert.True(result.Projection!.IsConsistent);
        Assert.Equal(4, Assert.Single(result.Projection.Orders).KnownFilledNativeAmount);
        var again = await recovery.RebuildAsync(Account, Epoch, From, Through, Complete);
        Assert.Equal(result.Projection.Orders, again.Projection!.Orders);
        Assert.NotEqual(result.Evidence.SnapshotId, again.Evidence.SnapshotId);
        Assert.Equal(0L, await Scalar(db.Source, "SELECT (SELECT count(*) FROM \"Orders\")+(SELECT count(*) FROM \"Fills\")"));
    }

    [Fact]
    public async Task FullScopeFilters_IsolateSameNativeIdsAndForeignIncidents()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var other = new AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("foreign"));
        var scopes = new[] { Account, other,
            new AccountKey(new("other-tenant"), Account.Venue, Account.Environment, Account.Account),
            new AccountKey(Account.Tenant, Account.Venue, OmsEnvironment.Mainnet, Account.Account),
            new AccountKey(Account.Tenant, Account.Venue, Account.Environment, Account.Account, new TradingSubaccountId("desk")) };
        foreach (var scope in scopes) await capture.CaptureAsync(Report() with { Scope = scope });
        var foreign = Fill() with { Scope = other };
        await capture.CaptureAsync(foreign);
        await capture.CaptureAsync(foreign with { Facts = foreign.Facts with { Price = 2001 },
            Observation = foreign.Observation with { CaptureId = Guid.NewGuid() } });
        var reader = new OmsPostgresPrivateRecoveryReader(db.Source);
        foreach (var scope in scopes.Where(x => x != other))
        {
            var result = await reader.ReadAsync(scope);
            Assert.True(result.IsUsable);
            Assert.Equal(scope, Assert.Single(result.Observations).Envelope.Scope);
        }
        Assert.False((await reader.ReadAsync(other)).IsUsable);
    }

    [Fact]
    public async Task QuarantineAndIncident_BlockCompositionEvenWhenAcceptedFactSurvives()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var fill = Fill();
        await capture.CaptureAsync(fill);
        await capture.CaptureAsync(fill with { Facts = fill.Facts with { Price = 2001 },
            Observation = fill.Observation with { CaptureId = Guid.NewGuid() } });
        var service = new DeribitDurableOrderRecovery(new OmsPostgresPrivateRecoveryReader(db.Source));
        var result = await service.RebuildAsync(Account, Epoch, From, Through, Complete);
        Assert.Null(result.Projection);
        Assert.Equal(2, result.Evidence.Observations.Count);
        Assert.Contains(OmsPrivateRecoveryReadProblem.QuarantinedObservation, result.Evidence.Problems);
        Assert.Contains(OmsPrivateRecoveryReadProblem.IntegrityIncident, result.Evidence.Problems);
    }

    [Fact]
    public async Task IncidentWithoutCollisionRows_StillBlocksSnapshot()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
        await Execute(db.Source, """
INSERT INTO oms_integrity_incidents(tenant_id,venue_id,environment,account_id,subaccount_id,kind,correlation)
VALUES ('tenant','DERIBIT',3,'account','','CommandCollision','fixture')
""");
        var result = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Account);
        Assert.False(result.IsUsable);
        Assert.Contains(OmsPrivateRecoveryReadProblem.IntegrityIncident, result.Problems);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task RowLimitNeverReturnsTruncatedUsableEvidence(int observationsOnly)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var first = Report();
        await capture.CaptureAsync(first);
        await capture.CaptureAsync(observationsOnly == 1
            ? first with { Observation = first.Observation with { CaptureId = Guid.NewGuid() } }
            : Fill());
        var result = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Account, 1);
        Assert.False(result.IsUsable);
        Assert.Empty(result.Observations);
        Assert.Contains(OmsPrivateRecoveryReadProblem.CapacityExceeded, result.Problems);
    }

    [Theory]
    [InlineData("UPDATE oms_inbox SET payload_sha256=decode(repeat('01',32),'hex')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_inbox SET event_json=jsonb_set(event_json,'{facts,price}','2001')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_sha256=decode(repeat('01',32),'hex')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET stream_epoch='22222222-2222-4222-8222-222222222222'", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET capture_id='22222222-2222-4222-8222-222222222222'", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET event_namespace='unknown'", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=jsonb_set(observation_json,'{venueEvent,account}','\"foreign\"')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=observation_json||'{\"unexpected\":1}'", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=observation_json#-'{venueEvent,facts,fee}'", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_inbox SET schema_version=3", OmsPrivateRecoveryReadProblem.UnsupportedSchema)]
    [InlineData("UPDATE oms_inbox SET event_json=jsonb_set(event_json,'{schemaVersion}','3')", OmsPrivateRecoveryReadProblem.UnsupportedSchema)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=jsonb_set(observation_json,'{captureSchemaVersion}','2')", OmsPrivateRecoveryReadProblem.UnsupportedSchema)]
    [InlineData("DELETE FROM oms_inbox", OmsPrivateRecoveryReadProblem.MissingAcceptedFact)]
    [InlineData("DELETE FROM oms_private_capture_observations", OmsPrivateRecoveryReadProblem.OrphanedAcceptedFact)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=observation_json||jsonb_build_object('large',repeat('x',140000))", OmsPrivateRecoveryReadProblem.CapacityExceeded)]
    [InlineData("UPDATE oms_inbox SET event_json=jsonb_set(event_json,'{account}','\"foreign\"')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    [InlineData("UPDATE oms_private_capture_observations SET observation_json=jsonb_set(observation_json,'{observation,source}','99')", OmsPrivateRecoveryReadProblem.InvalidRecord)]
    public async Task DamagedOrUnsupportedDurableData_FailsClosed(string mutation, OmsPrivateRecoveryReadProblem problem)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
        // Corruption injection only in an owned disposable clone. Production triggers stay intact.
        await Execute(db.Source, "ALTER TABLE oms_inbox DISABLE TRIGGER USER; ALTER TABLE oms_private_capture_observations DISABLE TRIGGER USER");
        await Execute(db.Source, mutation);
        await Execute(db.Source, "ALTER TABLE oms_inbox ENABLE TRIGGER USER; ALTER TABLE oms_private_capture_observations ENABLE TRIGGER USER");
        var result = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Account);
        Assert.False(result.IsUsable);
        Assert.Empty(result.Observations);
        Assert.Contains(problem, result.Problems);
    }

    [Fact]
    public async Task SelectOnlyRole_RequiresActualReadOnlyRepeatableReadTransaction()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
        var role = "to_app_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await using var admin = await db.Source.OpenConnectionAsync();
        await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{role}";
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT ON oms_inbox,oms_private_capture_observations,oms_integrity_incidents TO "{role}";
ALTER TABLE oms_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE oms_private_capture_observations ENABLE ROW LEVEL SECURITY;
CREATE POLICY fixture_read ON oms_inbox FOR SELECT TO "{role}" USING
 (current_setting('transaction_isolation')='repeatable read' AND current_setting('transaction_read_only')='on');
CREATE POLICY fixture_read ON oms_private_capture_observations FOR SELECT TO "{role}" USING
 (current_setting('transaction_isolation')='repeatable read' AND current_setting('transaction_read_only')='on');
""");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = password, Pooling = false };
            await using var readSource = NpgsqlDataSource.Create(cs.ConnectionString);
            var result = await new OmsPostgresPrivateRecoveryReader(readSource).ReadAsync(Account);
            Assert.True(result.IsUsable);
            Assert.Equal(1, result.AcceptedFactCount);
            Assert.Single(result.Observations);
            Assert.False((bool)(await Scalar(readSource, "SELECT has_table_privilege(current_user,'oms_inbox','INSERT')"))!);
        }
        finally
        {
            await OmsPostgresTestDatabase.Execute(admin, $"DROP POLICY fixture_read ON oms_inbox; DROP POLICY fixture_read ON oms_private_capture_observations; DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");
        }
    }

    [Fact]
    public async Task InFlightCapture_IsInvisibleAsAWhole_AndNextFullReadSeesLateCommit()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        await using var blocking = await db.Source.OpenConnectionAsync();
        await using var tx = await blocking.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE oms_private_capture_observations IN SHARE MODE", blocking, tx))
            await command.ExecuteNonQueryAsync();
        var app = "b4-reader-" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { ApplicationName = app, Pooling = false };
        await using var writeSource = NpgsqlDataSource.Create(cs.ConnectionString);
        var sending = new OmsPostgresPrivateEventCapture(writeSource).CaptureAsync(Report());
        var reader = new OmsPostgresPrivateRecoveryReader(db.Source);
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                await using var query = db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock')");
                query.Parameters.AddWithValue("app", app);
                if (await query.ExecuteScalarAsync() is true) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Owned fixture writer did not reach the blocked insert.");
                await Task.Delay(20);
            }
            var before = await reader.ReadAsync(Account);
            Assert.True(before.IsUsable);
            Assert.Equal(0, before.AcceptedFactCount);
            Assert.Empty(before.Observations);
        }
        finally { await tx.CommitAsync(); await sending; }
        await Execute(db.Source, "ALTER TABLE oms_private_capture_observations DISABLE TRIGGER USER; UPDATE oms_private_capture_observations SET recorded_at='2000-01-01T00:00:00Z'; ALTER TABLE oms_private_capture_observations ENABLE TRIGGER USER");
        var after = await reader.ReadAsync(Account);
        Assert.True(after.IsUsable);
        Assert.Single(after.Observations);
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); return await command.ExecuteScalarAsync(); }
}
