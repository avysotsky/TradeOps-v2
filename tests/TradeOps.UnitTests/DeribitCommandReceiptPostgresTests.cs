using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitCommandReceiptFixtures;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitCommandReceiptPostgresTests
{
    [Fact]
    public async Task OriginalV1CommandWithDecimalScaleRemainsReadableAndUnchanged()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var command = Command();
        command = command with { Payload = command.Payload with { Quantity = 1.000m, LimitPrice = 2000.00m } };
        var r = await Start(db, command);
        var digest = await Scalar(db.Source, "SELECT encode(payload_sha256,'hex') FROM oms_commands");
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(snapshot.IsUsable); Assert.Equal(r, Assert.Single(snapshot.Receipts));
        Assert.Equal(digest, await Scalar(db.Source, "SELECT encode(payload_sha256,'hex') FROM oms_commands"));
    }

    [Fact]
    public async Task ReceiptCommittedWhileReaderWaitsForIntentsIsNotMixedWithOldPrivateSnapshot()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(pooling: false); var r = await Start(db);
        await using var blocker = await db.Source.OpenConnectionAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("LOCK TABLE oms_intents IN ACCESS EXCLUSIVE MODE", blocker, tx))
            await locked.ExecuteNonQueryAsync();
        var app = "b4-receipt-" + Guid.NewGuid().ToString("N");
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
            Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r));
            await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(new(Account, r.Order,
                new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, OmsPrivateCaptureSource.WebSocket, "orders")));
        }
        finally { await tx.CommitAsync(); }
        var before = await reading;
        Assert.True(before.IsUsable); Assert.Empty(before.Receipts); Assert.Empty(before.Private.Observations);
        var after = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(after.IsUsable); Assert.Single(after.Receipts); Assert.Single(after.Private.Observations);
    }

    [Fact]
    public async Task CaptureOwnerCannotHaveReceiptWriteAuthority()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var role = "to_app_" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        await using var admin = await db.Source.OpenConnectionAsync();
        await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{role}"; GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO "{role}";
GRANT SELECT,INSERT ON oms_integrity_incidents TO "{role}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{role}";
GRANT INSERT ON oms_deribit_command_receipts_v1 TO "{role}";
""");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = password, Pooling = false };
            await using var capture = NpgsqlDataSource.Create(cs.ConnectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() => OmsPostgresPrivateCaptureOwner.AcquireAsync(capture, Account));
        }
        finally { await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\""); }
    }

    [Fact]
    public async Task CancelCannotClaimPlacementFromAnotherAggregate()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var place = await Start(db);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source); await writer.RecordAsync(place);
        var request = Command(); request = request with { Payload = request.Payload with {
            Kind = OmsJournalCommandKind.Cancel, TargetExchangeOrderId = place.Order.ExchangeOrderId } };
        var cancel = await Start(db, request, rpc: 2);
        Assert.Equal(DeribitCommandReceiptWriteResult.NotEligible, await writer.RecordAsync(cancel));
    }

    internal static async Task<DeribitJsonRpcCommandReceipt> Start(OmsPostgresTestDatabase db,
        OmsJournalCommandRequest? request = null, string nativeId = "native-1", long rpc = 1, Guid? session = null)
    {
        request ??= Command(); var journal = new OmsPostgresJournal(db.Source);
        Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(request));
        Assert.Equal(OmsJournalAttemptResult.Started, await journal.MarkDispatchStartedAsync(request.Scope, request.CommandId));
        var context = Receipt(request, nativeId, rpc, DateTimeOffset.UtcNow, session);
        return Parse(context, Response(context));
    }

    [Fact]
    public async Task CommittedDirectResponse_SurvivesFreshReaderAndLostAcknowledgementReplay()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await writer.RecordAsync(r));
        var scaled = r with { Command = r.Command with { Payload = r.Command.Payload with { Quantity = 1.000m } } };
        Assert.Equal(DeribitCommandReceiptWriteResult.Duplicate, await writer.RecordAsync(scaled));
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(snapshot.IsUsable); Assert.Equal(r, Assert.Single(snapshot.Receipts));
        Assert.Equal(OmsRecoveryDispatchPhase.DispatchStarted, Assert.Single(snapshot.Commands).Phase);
        Assert.Empty(snapshot.Private.Observations); Assert.Equal(1L, await Scalar(db.Source, "SELECT count(*) FROM oms_attempts"));
    }

    [Fact]
    public async Task RollbackBeforeCommit_LeavesNoReceiptAndOriginalDispatchState()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        await Assert.ThrowsAsync<IOException>(() => writer.RecordWithFaultAsync(r, _ => throw new IOException("fixture crash")));
        Assert.Equal(0L, await Scalar(db.Source, "SELECT count(*) FROM oms_deribit_command_receipts_v1"));
        Assert.Equal("DispatchStarted", await Scalar(db.Source, "SELECT dispatch_phase FROM oms_commands"));
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await writer.RecordAsync(r));
    }

    [Fact]
    public async Task FourConcurrentIdenticalReceipts_OneInsertAndThreeDuplicates()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => writer.RecordAsync(r)));
        Assert.Equal(1, results.Count(x => x == DeribitCommandReceiptWriteResult.Inserted));
        Assert.Equal(3, results.Count(x => x == DeribitCommandReceiptWriteResult.Duplicate));
        Assert.Equal(0L, await Scalar(db.Source, "SELECT count(*) FROM oms_integrity_incidents"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DifferentCommandCannotClaimNativeOrderOrRpcContext(bool rpcCollision)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var first = await Start(db);
        var second = await Start(db, nativeId: rpcCollision ? "native-2" : "native-1",
            rpc: rpcCollision ? first.RpcRequestId : 2, session: rpcCollision ? first.SessionId : Guid.NewGuid());
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        var outcomes = await Task.WhenAll(writer.RecordAsync(first), writer.RecordAsync(second));
        Assert.Contains(DeribitCommandReceiptWriteResult.Inserted, outcomes);
        Assert.Contains(DeribitCommandReceiptWriteResult.Collision, outcomes);
        Assert.Equal(1L, await Scalar(db.Source, "SELECT count(*) FROM oms_deribit_command_receipts_v1"));
        Assert.Equal(1L, await Scalar(db.Source, "SELECT count(*) FROM oms_integrity_incidents"));
        Assert.False((await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account)).IsUsable);
    }

    [Fact]
    public async Task SameCommandDifferentResponse_PreservesFirstAndQuarantinesConflict()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        await writer.RecordAsync(r);
        Assert.Equal(DeribitCommandReceiptWriteResult.Collision, await writer.RecordAsync(r with { RpcRequestId = 2 }));
        Assert.Equal(1L, await Scalar(db.Source, "SELECT rpc_request_id FROM oms_deribit_command_receipts_v1"));
        Assert.Equal("CommandCollision:deribit-receipt-v1:" + r.Command.CommandId.Value.ToString("D"),
            await Scalar(db.Source, "SELECT kind || ':' || correlation FROM oms_integrity_incidents"));
        Assert.False((await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account)).IsUsable);
    }

    [Theory]
    [InlineData("missing")] [InlineData("unsent")] [InlineData("rejected")] [InlineData("changed-command")]
    public async Task MissingDispatchOrDifferentImmutableCommand_CannotAcquireBinding(string mode)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var request = Command(); var r = Receipt(request, sent: DateTimeOffset.UtcNow);
        var journal = new OmsPostgresJournal(db.Source);
        if (mode != "missing") await journal.PersistAsync(request);
        if (mode is "rejected" or "changed-command")
        {
            await journal.MarkDispatchStartedAsync(Account, request.CommandId);
            r = Receipt(request, sent: DateTimeOffset.UtcNow);
        }
        if (mode == "rejected") await journal.RecordOutcomeAsync(Account, request.CommandId, OmsJournalDispatchOutcome.Rejected);
        if (mode == "changed-command") r = r with { Command = r.Command with { Payload = r.Command.Payload with { RiskDecisionReference = "changed" } } };
        Assert.Equal(DeribitCommandReceiptWriteResult.NotEligible, await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r));
        Assert.Equal(0L, await Scalar(db.Source, "SELECT count(*) FROM oms_deribit_command_receipts_v1"));
    }

    [Fact]
    public async Task AddressedCancelRequiresEarlierPlacementReceiptOfSameAggregate()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var place = await Start(db);
        var cancel = await Start(db, Cancel(place.Command), rpc: 2);
        var writer = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        Assert.Equal(DeribitCommandReceiptWriteResult.NotEligible, await writer.RecordAsync(cancel));
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await writer.RecordAsync(place));
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await writer.RecordAsync(cancel));
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.True(snapshot.IsUsable); Assert.Equal(2, snapshot.Receipts.Count);
        Assert.Equal(2L, await Scalar(db.Source, "SELECT count(*) FROM oms_attempts"));
    }

    [Theory]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET native_order_id='changed'")]
    [InlineData("DELETE FROM oms_deribit_command_receipts_v1")]
    [InlineData("TRUNCATE oms_deribit_command_receipts_v1")]
    public async Task NativeEvidenceIsAppendOnlyEvenForFixtureOwner(string mutation)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(db.Source, mutation));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(1L, await Scalar(db.Source, "SELECT count(*) FROM oms_deribit_command_receipts_v1"));
    }

    [Theory]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET native_order_id='changed'")]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET receipt_sha256=decode(repeat('00',32),'hex')")]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET command_sha256=decode(repeat('00',32),'hex')")]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET receipt_json=jsonb_set(receipt_json,'{schemaVersion}','2')")]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET receipt_json=jsonb_set(receipt_json,'{command,account}','\"foreign\"')")]
    [InlineData("UPDATE oms_deribit_command_receipts_v1 SET receipt_json=receipt_json || '{\"extra\":true}'")]
    public async Task OwnerInjectedCorruptionBlocksEntireCombinedSnapshot(string mutation)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        await Execute(db.Source, "ALTER TABLE oms_deribit_command_receipts_v1 DISABLE TRIGGER USER; " + mutation + "; ALTER TABLE oms_deribit_command_receipts_v1 ENABLE TRIGGER USER");
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account);
        Assert.False(snapshot.IsUsable); Assert.Empty(snapshot.Receipts); Assert.Empty(snapshot.Commands);
        Assert.Contains(OmsRecoveryLedgerProblem.InvalidRecord, snapshot.Problems);
    }

    [Fact]
    public async Task MainnetScopeIsSupportedInIsolatedCloneAndDoesNotLeakIntoOtherAccount()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var request = Command();
        var scope = new AccountKey(Account.Tenant, Account.Venue, OmsEnvironment.Mainnet, Account.Account, new TradingSubaccountId("desk"));
        var r = await Start(db, request with { Scope = scope });
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        var reader = new OmsPostgresRecoveryLedgerReader(db.Source);
        Assert.Equal(r, Assert.Single((await reader.ReadAsync(scope)).Receipts));
        Assert.Empty((await reader.ReadAsync(Account)).Receipts);
    }

    [Fact]
    public async Task NonemptyReceiptMigrationRefusesDestructiveRollback()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        await using var context = new TradeOpsDbContext(new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(db.ConnectionString).Options);
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync("20261104120000_AddOmsPrivateCaptureObservations"));
        Assert.Contains("preserve native command evidence", error.MessageText);
        Assert.Equal(1L, await Scalar(db.Source, "SELECT count(*) FROM oms_deribit_command_receipts_v1"));
    }

    [Fact]
    public async Task SelectOnlyReaderConsumesActualReceiptInReadOnlyRepeatableRead()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var r = await Start(db);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        var role = "to_app_" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        await using var admin = await db.Source.OpenConnectionAsync();
        await OmsPostgresTestDatabase.Execute(admin, $"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{role}"; GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT ON oms_commands,oms_attempts,oms_outbox,oms_intents,oms_aggregates,
 oms_inbox,oms_private_capture_observations,oms_integrity_incidents,oms_deribit_command_receipts_v1 TO "{role}";
ALTER TABLE oms_deribit_command_receipts_v1 ENABLE ROW LEVEL SECURITY;
CREATE POLICY fixture_read ON oms_deribit_command_receipts_v1 FOR SELECT TO "{role}" USING
 (current_setting('transaction_isolation')='repeatable read' AND current_setting('transaction_read_only')='on');
""");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = password, Pooling = false };
            await using var read = NpgsqlDataSource.Create(cs.ConnectionString);
            var result = await new OmsPostgresRecoveryLedgerReader(read).ReadAsync(Account);
            Assert.True(result.IsUsable); Assert.Equal(r, Assert.Single(result.Receipts));
        }
        finally { await OmsPostgresTestDatabase.Execute(admin, $"DROP POLICY fixture_read ON oms_deribit_command_receipts_v1; DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\""); }
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); return await command.ExecuteScalarAsync(); }
    private static async Task Execute(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
}
