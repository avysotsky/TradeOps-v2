using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Oms;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Opt-in real PostgreSQL integration tests. Requires TRADEOPS_B2_POSTGRES set to
/// a disposable PostgreSQL 17 admin connection string. The acceptance runner MUST
/// set the variable; absent variable is NOT proof of Postgres PASS.
/// </summary>
public sealed class OmsJournalPostgresTests
{
    private static AccountKey Scope(string account = "account-1", string venue = "Deribit",
        string? sub = null) => new(new TenantId("tenant"), new VenueId(venue),
            OmsEnvironment.Testnet, new TradingAccountId(account),
            sub is null ? null : new TradingSubaccountId(sub));

    private static OmsJournalCommandRequest Request(AccountKey? scope = null,
        Guid? commandId = null, Guid? aggregateId = null, Guid? intentId = null,
        long expected = 0, decimal quantity = 1m) =>
        new(scope ?? Scope(),
            new OmsLocalOrderId(aggregateId ?? Guid.Parse("10000000-0000-4000-8000-000000000001")),
            new OrderIntentId(intentId ?? Guid.Parse("20000000-0000-4000-8000-000000000001")),
            new OmsCommandId(commandId ?? Guid.Parse("30000000-0000-4000-8000-000000000001")),
            new OrderVersion(expected),
            new OmsJournalCommandPayload(OmsJournalCommandKind.Place, "BTC-PERPETUAL",
                OrderSide.Buy, OrderType.Limit, quantity, OmsQuantityUnit.Contracts,
                65000m, null, "GTC", false, true,
                "synthetic-test", "strategy-1", "risk-verdict-1",
                new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task ConcurrentIdenticalCommand_ExactlyOneJournalAndOutbox()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => new OmsPostgresJournal(ds).PersistAsync(request)));
            Assert.Equal(1, outcomes.Count(x => x == OmsJournalWriteResult.Created));
            Assert.Equal(11, outcomes.Count(x => x == OmsJournalWriteResult.Duplicate));
            Assert.Equal(1, await Count(cs, "oms_intents"));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(1, await ScalarLong(cs, "SELECT version FROM oms_aggregates"));
        });
    }

    [Fact]
    public async Task SameIdDifferentPayload_IsDurablyQuarantinedNotOverwritten()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var original = Request();
            Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(original));
            var collision = original with { Payload = original.Payload with { Quantity = 7m } };
            Assert.Equal(OmsJournalWriteResult.Collision, await journal.PersistAsync(collision));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
            Assert.Equal(1, await ScalarLong(cs, "SELECT version FROM oms_aggregates"));
        });
    }

    [Fact]
    public async Task SameRemoteAndLocalIds_AreLegalAcrossDifferentAccountsAndVenues()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var baseRequest = Request();
            var otherAccount = Request(scope: Scope(account: "account-2"));
            var otherVenue = Request(scope: Scope(venue: "CoinCall"));
            var otherSubaccount = Request(scope: Scope(sub: "desk-1"));
            foreach (var item in new[] { baseRequest, otherAccount, otherVenue, otherSubaccount })
                Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(item));
            Assert.Equal(4, await Count(cs, "oms_commands"));
            Assert.Equal(4, await Count(cs, "oms_outbox"));
            Assert.Equal(4, await Count(cs, "oms_aggregates"));
        });
    }

    [Fact]
    public async Task BeforeCommitCrash_RollsBackAggregateIntentCommandAndOutbox()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var request = Request();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.PersistAsync(request, _ => throw new InvalidOperationException("kill-before-commit")));
            foreach (var table in new[] { "oms_aggregates", "oms_intents", "oms_commands", "oms_outbox" })
                Assert.Equal(0, await Count(cs, table));
            Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(request));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
        });
    }

    [Fact]
    public async Task ConcurrentDifferentCommandsWithSameExpectedVersion_OneWinsCAS()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var first = Request();
            var second = Request(commandId: Guid.Parse("30000000-0000-4000-8000-000000000002"),
                intentId: Guid.Parse("20000000-0000-4000-8000-000000000002"));
            var responses = await Task.WhenAll(
                new OmsPostgresJournal(ds).PersistAsync(first),
                new OmsPostgresJournal(ds).PersistAsync(second));
            Assert.Single(responses.Where(x => x == OmsJournalWriteResult.Created));
            Assert.Single(responses.Where(x => x == OmsJournalWriteResult.StaleVersion));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(1, await ScalarLong(cs, "SELECT version FROM oms_aggregates"));
        });
    }

    [Fact]
    public async Task StaleAggregateVersion_CannotAppendAnotherCommandOrOutbox()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(Request()));
            var next = Request(commandId: Guid.NewGuid(), expected: 0);
            Assert.Equal(OmsJournalWriteResult.StaleVersion, await journal.PersistAsync(next));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
        });
    }

    [Fact]
    public async Task DispatchStartedCommitThenRestart_RefusesResendAndRetainsRecovery()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            var command = request.CommandId;
            Assert.Equal(OmsJournalWriteResult.Created,
                await new OmsPostgresJournal(ds).PersistAsync(request));
            Assert.Equal(OmsJournalAttemptResult.Started,
                await new OmsPostgresJournal(ds).MarkDispatchStartedAsync(request.Scope, command));

            // New repository instance simulates process death after durable send boundary,
            // before ACK. No transport client exists in the tested assembly.
            var restarted = new OmsPostgresJournal(ds);
            Assert.Equal(OmsJournalAttemptResult.AlreadyStartedOrResolved,
                await restarted.MarkDispatchStartedAsync(request.Scope, command));
            Assert.Equal(1, await Count(cs, "oms_attempts"));
            Assert.Equal("DispatchStarted", await ScalarString(cs,
                "SELECT dispatch_phase FROM oms_commands"));
            Assert.Equal("NeedsRecovery", await ScalarString(cs,
                "SELECT state FROM oms_outbox"));
            Assert.False(await restarted.RecordOutcomeAsync(request.Scope,
                new OmsCommandId(Guid.NewGuid()), OmsJournalDispatchOutcome.Acknowledged));
        });
    }

    [Fact]
    public async Task LostAck_RemainsAmbiguousAndCannotOpenNewAttempt()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            var journal = new OmsPostgresJournal(ds);
            await journal.PersistAsync(request);
            await journal.MarkDispatchStartedAsync(request.Scope, request.CommandId);
            Assert.True(await journal.RecordOutcomeAsync(request.Scope, request.CommandId,
                OmsJournalDispatchOutcome.Ambiguous));
            Assert.Equal(OmsJournalAttemptResult.AlreadyStartedOrResolved,
                await new OmsPostgresJournal(ds).MarkDispatchStartedAsync(
                    request.Scope, request.CommandId));
            Assert.Equal(1, await Count(cs, "oms_attempts"));
            Assert.Equal("NeedsRecovery", await ScalarString(cs,
                "SELECT state FROM oms_outbox"));
        });
    }

    [Fact]
    public async Task InboxDeduplicatesExactReplayAndQuarantinesConflictingExecution()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var j = new OmsPostgresJournal(ds);
            var scope = Scope();
            var first = Event(scope);
            Assert.Equal(OmsJournalInboxResult.Inserted, await j.PersistInboxAsync(first));
            Assert.Equal(OmsJournalInboxResult.Duplicate, await j.PersistInboxAsync(first));
            Assert.Equal(OmsJournalInboxResult.Collision,
                await j.PersistInboxAsync(first with { Quantity = 3m }));
            Assert.Equal(OmsJournalInboxResult.Inserted,
                await j.PersistInboxAsync(Event(Scope(account: "second"))));
            Assert.Equal(2, await Count(cs, "oms_inbox"));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
        });
    }

    [Fact]
    public async Task ConcurrentExecutionInbox_ExactlyOneInserted()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var item = Event(Scope());
            var results = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => new OmsPostgresJournal(ds).PersistInboxAsync(item)));
            Assert.Equal(1, results.Count(x => x == OmsJournalInboxResult.Inserted));
            Assert.Equal(15, results.Count(x => x == OmsJournalInboxResult.Duplicate));
            Assert.Equal(1, await Count(cs, "oms_inbox"));
        });
    }

    [Fact]
    public async Task ExistingIntentCannotBeReboundToDifferentAggregate()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(Request()));
            var differentAggregate = Request(
                aggregateId: Guid.Parse("10000000-0000-4000-8000-000000000009"),
                commandId: Guid.Parse("30000000-0000-4000-8000-000000000009"));
            Assert.Equal(OmsJournalWriteResult.Collision,
                await journal.PersistAsync(differentAggregate));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
        });
    }

    [Fact]
    public async Task ImmutableDatabaseTrigger_PreventsPostCommitPayloadTampering()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            await new OmsPostgresJournal(ds).PersistAsync(request);
            await using var db = new NpgsqlConnection(cs);
            await db.OpenAsync();
            await using var tamper = new NpgsqlCommand(
                "UPDATE oms_commands SET request_json='{}'::jsonb", db);
            await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
        });
    }

    [Fact]
    public async Task MetadataLease_HasSingleOwnerAndMonotonicGeneration_NotPhysicalFencing()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var scope = Scope();
            var owners = await Task.WhenAll(
                journal.TryAcquireMetadataLeaseAsync(scope, Guid.NewGuid(), TimeSpan.FromMinutes(1)),
                journal.TryAcquireMetadataLeaseAsync(scope, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
            Assert.Single(owners.Where(x => x.HasValue));
            Assert.Equal(1L, owners.Single(x => x.HasValue)!.Value);
            await using (var db = new NpgsqlConnection(cs))
            {
                await db.OpenAsync();
                await using var expire = new NpgsqlCommand(
                    "UPDATE oms_account_leases SET expires_at=clock_timestamp()-interval '1 second'", db);
                await expire.ExecuteNonQueryAsync();
            }
            Assert.Equal(2L, await journal.TryAcquireMetadataLeaseAsync(
                scope, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
        });
    }

    private static OmsJournalInboxEvent Event(AccountKey scope) =>
        new(scope, "Deribit:trade-id", "trade-42", OmsJournalEventKind.Execution,
            new OmsLocalOrderId(Guid.Parse("10000000-0000-4000-8000-000000000001")),
            "BTC-PERPETUAL", 2m, 60000m,
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private static async Task WithPostgres(Func<NpgsqlDataSource, string, Task> act)
    {
        var admin = Environment.GetEnvironmentVariable("TRADEOPS_B2_POSTGRES");
        if (string.IsNullOrWhiteSpace(admin))
            return; // NOT an acceptance pass; runner must set TRADEOPS_B2_POSTGRES.

        var dbName = "b2_journal_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
        var test = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        { Database = dbName, Pooling = false };
        await using var adminDb = new NpgsqlConnection(builder.ConnectionString);
        await adminDb.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminDb))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql(test.ConnectionString).Options;
            await using (var context = new TradeOpsDbContext(options))
                await context.Database.MigrateAsync();
            await using var ds = NpgsqlDataSource.Create(test.ConnectionString);
            await act(ds, test.ConnectionString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", adminDb);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> Count(string cs, string table) =>
        await ScalarLong(cs, $"SELECT count(*) FROM {table}");

    private static async Task<long> ScalarLong(string cs, string sql)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string> ScalarString(string cs, string sql)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }
}
