using System.Diagnostics;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsRecoveryLedgerPostgresTests
{
    internal static OmsJournalCommandRequest Command(AccountKey? scope = null) => new(scope ?? Account,
        new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid()), new(0),
        new(OmsJournalCommandKind.Place, "ETH-PERPETUAL", OrderSide.Buy, OrderType.Limit, 1,
            OmsQuantityUnit.Contracts, 2000, null, "GTC", false, true, "fixture", "operator", "risk",
            From));

    [Fact]
    public async Task AllPhases_CombinedSnapshotPreservesCanonicalJournalAndDoesNotWrite()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var journal = new OmsPostgresJournal(db.Source);
        foreach (var phase in Enum.GetValues<OmsRecoveryDispatchPhase>())
        {
            var request = Command();
            await journal.PersistAsync(request);
            if (phase == OmsRecoveryDispatchPhase.PersistedNotSent) continue;
            await journal.MarkDispatchStartedAsync(Account, request.CommandId);
            if (phase != OmsRecoveryDispatchPhase.DispatchStarted)
                await journal.RecordOutcomeAsync(Account, request.CommandId,
                    Enum.Parse<OmsJournalDispatchOutcome>(phase.ToString()));
        }
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
        var before = await Digest(db.Source);
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(result.IsUsable);
        Assert.Equal(5, result.Commands.Count);
        Assert.Equal(Enum.GetValues<OmsRecoveryDispatchPhase>().Order(), result.Commands.Select(x => x.Phase).Order());
        Assert.Single(result.Private.Observations);
        Assert.NotEqual(Guid.Empty, result.Private.SnapshotId);
        Assert.Equal(before, await Digest(db.Source));
    }

    [Fact]
    public async Task PlaceCancelReplace_PreservesAggregateChainAndExplicitTargets()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var journal = new OmsPostgresJournal(db.Source);
        var first = Command();
        await journal.PersistAsync(first);
        foreach (var (kind, version) in new[] { (OmsJournalCommandKind.Cancel, 1), (OmsJournalCommandKind.Replace, 2) })
            await journal.PersistAsync(first with { CommandId = new(Guid.NewGuid()), ExpectedAggregateVersion = new(version),
                Payload = first.Payload with { Kind = kind, TargetExchangeOrderId = "native-order" } });
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(result.IsUsable);
        Assert.Equal(new long[] { 1, 2, 3 }, result.Commands.Select(x => x.AggregateVersion));
        Assert.All(result.Commands.Skip(1), x => Assert.Equal("native-order", x.Request.Payload.TargetExchangeOrderId));
    }

    [Fact]
    public async Task FullScopeIsolation_DoesNotImportForeignCommandsOrIncidents()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var request = Command();
        var scopes = new[] { Account,
            new AccountKey(new("foreign"), Account.Venue, Account.Environment, Account.Account),
            new AccountKey(Account.Tenant, new("other"), Account.Environment, Account.Account),
            new AccountKey(Account.Tenant, Account.Venue, OmsEnvironment.Mainnet, Account.Account),
            new AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("foreign")),
            new AccountKey(Account.Tenant, Account.Venue, Account.Environment, Account.Account, new TradingSubaccountId("desk")) };
        var journal = new OmsPostgresJournal(db.Source);
        foreach (var scope in scopes) await journal.PersistAsync(request with { Scope = scope });
        await journal.PersistAsync(request with { Scope = scopes[1], Payload = request.Payload with { Quantity = 2 } });
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(result.IsUsable);
        Assert.Equal(request, Assert.Single(result.Commands).Request);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapacityExceeded_NeverReturnsUsablePartialUniverse(bool privateSide)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var journal = new OmsPostgresJournal(db.Source);
        await journal.PersistAsync(Command());
        if (privateSide)
        {
            var capture = new OmsPostgresPrivateEventCapture(db.Source);
            await capture.CaptureAsync(Report()); await capture.CaptureAsync(Fill());
        }
        else await journal.PersistAsync(Command());
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account, 1);
        Assert.False(result.IsUsable);
        if (!privateSide) { Assert.Empty(result.Commands); Assert.Contains(OmsRecoveryLedgerProblem.CapacityExceeded, result.Problems); }
        else Assert.Contains(OmsPrivateRecoveryReadProblem.CapacityExceeded, result.Private.Problems);
    }

    [Theory]
    [InlineData("UPDATE oms_commands SET schema_version=9")]
    [InlineData("UPDATE oms_commands SET request_json=jsonb_set(request_json,'{schemaVersion}','9')")]
    [InlineData("UPDATE oms_commands SET payload_sha256=decode(repeat('00',32),'hex')")]
    [InlineData("UPDATE oms_commands SET request_json=jsonb_set(request_json,'{account}','\"foreign\"')")]
    [InlineData("UPDATE oms_commands SET request_json=request_json || '{\"extra\":true}'")]
    [InlineData("UPDATE oms_commands SET request_json=request_json #- '{command,fixClOrdId}'")]
    [InlineData("UPDATE oms_commands SET request_json=request_json || jsonb_build_object('oversize',repeat('x',17000))")]
    [InlineData("DELETE FROM oms_outbox")]
    [InlineData("UPDATE oms_outbox SET state='Resolved'")]
    [InlineData("UPDATE oms_commands SET dispatch_phase='Acknowledged'")]
    [InlineData("UPDATE oms_intents SET provenance='changed'")]
    [InlineData("UPDATE oms_aggregates SET version=2")]
    [InlineData("INSERT INTO oms_attempts SELECT tenant_id,venue_id,environment,account_id,subaccount_id,command_id,1,'DispatchStarted',clock_timestamp(),clock_timestamp() FROM oms_commands")]
    [InlineData("UPDATE oms_commands SET aggregate_version=2,expected_version=1")]
    public async Task CorruptJournalOrLinks_BlockWithoutRepair(string mutation)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresJournal(db.Source).PersistAsync(Command());
        // Intentional corruption is possible only with the disposable fixture's owner.
        // Normal journal APIs must continue to reject every immutable mutation.
        await Execute(db.Source, """
ALTER TABLE oms_commands DISABLE TRIGGER USER;
ALTER TABLE oms_outbox DISABLE TRIGGER USER;
ALTER TABLE oms_intents DISABLE TRIGGER USER;
""" + mutation + """
;
ALTER TABLE oms_commands ENABLE TRIGGER USER;
ALTER TABLE oms_outbox ENABLE TRIGGER USER;
ALTER TABLE oms_intents ENABLE TRIGGER USER;
""");
        var before = await Digest(db.Source);
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.False(result.IsUsable); Assert.Empty(result.Commands); Assert.NotEmpty(result.Problems);
        Assert.Equal(before, await Digest(db.Source));
    }

    [Fact]
    public async Task VersionedAggregateWithoutCommands_IsNotAnEmptyUsableJournal()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresJournal(db.Source).PersistAsync(Command());
        await Execute(db.Source, """
ALTER TABLE oms_outbox DISABLE TRIGGER USER; DELETE FROM oms_outbox; ALTER TABLE oms_outbox ENABLE TRIGGER USER;
ALTER TABLE oms_commands DISABLE TRIGGER USER; DELETE FROM oms_commands; ALTER TABLE oms_commands ENABLE TRIGGER USER;
""");
        var result = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.False(result.IsUsable); Assert.Contains(OmsRecoveryLedgerProblem.InvalidRecord, result.Problems);
    }

    [Fact]
    public async Task ReaderBlockedBetweenPrivateAndCommands_SeesOneMvccSnapshot()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false);
        await new OmsPostgresJournal(db.Source).PersistAsync(Command());
        await using var blocking = await db.Source.OpenConnectionAsync();
        await using var tx = await blocking.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("LOCK TABLE oms_commands IN ACCESS EXCLUSIVE MODE", blocking, tx))
            await locked.ExecuteNonQueryAsync();
        var app = "b4-ledger-" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { ApplicationName = app, Pooling = false };
        await using var readerSource = NpgsqlDataSource.Create(cs.ConnectionString);
        var reading = new OmsPostgresRecoveryLedgerReader(readerSource).ReadAsync(Account);
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                await using var check = db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock')");
                check.Parameters.AddWithValue("app", app);
                if (await check.ExecuteScalarAsync() is true) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5)); await Task.Delay(20);
            }
            await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
            await using var update = new NpgsqlCommand("""
UPDATE oms_commands SET dispatch_phase='DispatchStarted',updated_at=clock_timestamp();
UPDATE oms_outbox SET state='NeedsRecovery';
INSERT INTO oms_attempts SELECT tenant_id,venue_id,environment,account_id,subaccount_id,command_id,1,
 'DispatchStarted',clock_timestamp(),clock_timestamp() FROM oms_commands;
""", blocking, tx);
            await update.ExecuteNonQueryAsync();
        }
        finally { await tx.CommitAsync(); }
        var before = await reading;
        Assert.True(before.IsUsable); Assert.Empty(before.Private.Observations);
        Assert.Equal(OmsRecoveryDispatchPhase.PersistedNotSent, Assert.Single(before.Commands).Phase);
        var after = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(after.IsUsable); Assert.Single(after.Private.Observations);
        Assert.Equal(OmsRecoveryDispatchPhase.DispatchStarted, Assert.Single(after.Commands).Phase);
        Assert.NotEqual(before.Private.SnapshotId, after.Private.SnapshotId);
    }

    [Fact]
    public async Task SelectOnlyRole_RequiresReadOnlyRepeatableReadForBothSides()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresJournal(db.Source).PersistAsync(Command());
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(Report());
        var role = "to_app_" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        await using var admin = await db.Source.OpenConnectionAsync();
        await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{role}";
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT ON oms_commands,oms_attempts,oms_outbox,oms_intents,oms_aggregates,
 oms_inbox,oms_private_capture_observations,oms_integrity_incidents,oms_deribit_command_receipts_v1 TO "{role}";
ALTER TABLE oms_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE oms_inbox ENABLE ROW LEVEL SECURITY;
CREATE POLICY fixture_read ON oms_commands FOR SELECT TO "{role}" USING
 (current_setting('transaction_isolation')='repeatable read' AND current_setting('transaction_read_only')='on');
CREATE POLICY fixture_read ON oms_inbox FOR SELECT TO "{role}" USING
 (current_setting('transaction_isolation')='repeatable read' AND current_setting('transaction_read_only')='on');
""");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = password, Pooling = false };
            await using var readSource = NpgsqlDataSource.Create(cs.ConnectionString);
            var result = await new OmsPostgresRecoveryLedgerReader(readSource).ReadAsync(Account);
            Assert.True(result.IsUsable); Assert.Single(result.Commands); Assert.Single(result.Private.Observations);
            await using var rights = readSource.CreateCommand("SELECT has_table_privilege(current_user,'oms_commands','UPDATE') OR has_table_privilege(current_user,'oms_inbox','INSERT')");
            Assert.Equal(false, await rights.ExecuteScalarAsync());
        }
        finally { await OmsPostgresTestDatabase.Execute(admin, $"DROP POLICY fixture_read ON oms_commands; DROP POLICY fixture_read ON oms_inbox; DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\""); }
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }

    private static async Task<string> Digest(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
SELECT jsonb_build_array(
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY command_id) FROM oms_commands t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY command_id,attempt_number) FROM oms_attempts t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY command_id) FROM oms_outbox t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY event_namespace,event_id) FROM oms_inbox t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY capture_id) FROM oms_private_capture_observations t))::text
""");
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
