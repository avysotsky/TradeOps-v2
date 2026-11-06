using Npgsql;
using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsEvidenceVersionPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CounterRollbackPreservesLegacyJournalAndRefusesExistingRecoveryHistory(bool recoveryHistory)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await new OmsPostgresJournal(db.Source).PersistAsync(OmsRecoveryLedgerPostgresTests.Command());
        await using var digestQuery = db.Source.CreateCommand("SELECT encode(payload_sha256,'hex') FROM oms_commands");
        var digest = await digestQuery.ExecuteScalarAsync();
        if (recoveryHistory) await new OmsPostgresRecoveryRunStore(db.Source).BeginAsync(DeribitRecoveryRunPostgresTests.Start());
        await using var context = new TradeOpsDbContext(new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql(db.ConnectionString).Options);
        if (recoveryHistory)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync("20261105210000_AddOmsRecoveryRunsV1"));
            Assert.Contains("preserve scoped recovery history", error.MessageText);
            Assert.NotNull(await new OmsPostgresRecoveryRunStore(db.Source).ReadHeadAsync(Account));
        }
        else
        {
            await context.Database.MigrateAsync("20261105210000_AddOmsRecoveryRunsV1");
            await using var gone = db.Source.CreateCommand("SELECT to_regclass('public.oms_evidence_versions_v1') IS NULL");
            Assert.Equal(true, await gone.ExecuteScalarAsync());
        }
        Assert.Equal(digest, await digestQuery.ExecuteScalarAsync());
        await context.Database.MigrateAsync();
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Account);
        Assert.True(snapshot.IsUsable); Assert.Single(snapshot.Commands); Assert.True(snapshot.EvidenceVersion > 0);
        Assert.Equal(digest, await digestQuery.ExecuteScalarAsync());
    }

    [Fact]
    public async Task JournalAndCaptureMutationsAdvanceSameScopedVersionButIdenticalRetryDoesNot()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var reader = new OmsPostgresRecoveryLedgerReader(db.Source, includeEvidenceVersion: true);
        var command = OmsRecoveryLedgerPostgresTests.Command(); var journal = new OmsPostgresJournal(db.Source);
        Assert.Equal(0, (await reader.ReadAsync(Account)).EvidenceVersion);
        await journal.PersistAsync(command); var persisted = (await reader.ReadAsync(Account)).EvidenceVersion;
        Assert.True(persisted > 0); await journal.PersistAsync(command); Assert.Equal(persisted, (await reader.ReadAsync(Account)).EvidenceVersion);
        await journal.MarkDispatchStartedAsync(Account, command.CommandId); var sent = (await reader.ReadAsync(Account)).EvidenceVersion; Assert.True(sent > persisted);
        await journal.RecordOutcomeAsync(Account, command.CommandId, OmsJournalDispatchOutcome.Acknowledged);
        var ack = (await reader.ReadAsync(Account)).EvidenceVersion; Assert.True(ack > sent);
        var capture = new OmsPostgresPrivateEventCapture(db.Source); var fact = Report(); await capture.CaptureAsync(fact);
        var captured = (await reader.ReadAsync(Account)).EvidenceVersion; Assert.True(captured > ack);
        await capture.CaptureAsync(fact); Assert.Equal(captured, (await reader.ReadAsync(Account)).EvidenceVersion);
        Assert.Null((await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Account)).EvidenceVersion);
        var foreign = new TradeOps.Domain.Oms.AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("other-account"));
        Assert.Equal(0, (await reader.ReadAsync(foreign)).EvidenceVersion);
    }

    [Fact]
    public async Task RollbackLeavesNeitherEvidenceNorVersionAndUncommittedCounterIsNotVisible()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var reader = new OmsPostgresRecoveryLedgerReader(db.Source, true);
        await Assert.ThrowsAsync<IOException>(() => new OmsPostgresJournal(db.Source).PersistAsync(OmsRecoveryLedgerPostgresTests.Command(), async _ => {
            var during = await reader.ReadAsync(Account); Assert.Equal(0, during.EvidenceVersion); Assert.Empty(during.Commands);
            throw new IOException("synthetic interrupted commit");
        }));
        var after = await reader.ReadAsync(Account); Assert.Equal(0, after.EvidenceVersion); Assert.Empty(after.Commands);
    }

    [Fact]
    public async Task ScopedVersionLockArbitratesCommitWithoutUsingMaxSequenceAsCheckpoint()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var owner = await db.Source.OpenConnectionAsync(); await using var hold = await owner.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("""
INSERT INTO oms_evidence_versions_v1 (tenant_id,venue_id,environment,account_id,subaccount_id)
VALUES (@tenant,@venue,@environment,@account,'')
""", owner, hold))
        {
            create.Parameters.AddWithValue("tenant", Account.Tenant.Value); create.Parameters.AddWithValue("venue", Account.Venue.Value);
            create.Parameters.AddWithValue("environment", (short)Account.Environment); create.Parameters.AddWithValue("account", Account.Account.Value);
            await create.ExecuteNonQueryAsync();
        }
        var name = "evidence-version-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = false, ApplicationName = name };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var writing = new OmsPostgresJournal(source).PersistAsync(OmsRecoveryLedgerPostgresTests.Command());
        var committed = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                await using var probe = db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND application_name=@app AND wait_event_type='Lock')");
                probe.Parameters.AddWithValue("app", name);
                if (await probe.ExecuteScalarAsync(deadline.Token) is true) break;
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(writing.IsCompleted); await hold.CommitAsync(); committed = true;
        }
        finally { if (!committed) await hold.RollbackAsync(); }
        Assert.Equal(OmsJournalWriteResult.Created, await writing);
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Account);
        Assert.True(snapshot.EvidenceVersion > 0); Assert.Single(snapshot.Commands);
    }
}
