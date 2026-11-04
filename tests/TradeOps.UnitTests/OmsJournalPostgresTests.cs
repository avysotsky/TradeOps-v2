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
    public async Task ConcurrentSameCommandIdWithDifferentPayload_ExactlyOneWinsAndOthersAreClassified()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var first = Request();
            var changed = first with { Payload = first.Payload with { Quantity = 4m } };
            var results = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(i => new OmsPostgresJournal(ds).PersistAsync(i % 2 == 0 ? first : changed)));
            Assert.Equal(1, results.Count(x => x == OmsJournalWriteResult.Created));
            Assert.Equal(5, results.Count(x => x == OmsJournalWriteResult.Duplicate));
            Assert.Equal(6, results.Count(x => x == OmsJournalWriteResult.Collision));
            Assert.Equal(1, await Count(cs, "oms_aggregates"));
            Assert.Equal(1, await Count(cs, "oms_intents"));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(6, await Count(cs, "oms_integrity_incidents"));
        });
    }

    [Fact]
    public async Task ConcurrentSameIntentDifferentAggregate_QuarantinesConflictWithoutGhostRows()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var first = Request();
            var other = Request(
                aggregateId: Guid.Parse("10000000-0000-4000-8000-000000000098"),
                commandId: Guid.Parse("30000000-0000-4000-8000-000000000098"));
            var results = await Task.WhenAll(
                new OmsPostgresJournal(ds).PersistAsync(first),
                new OmsPostgresJournal(ds).PersistAsync(other));
            Assert.Single(results.Where(x => x == OmsJournalWriteResult.Created));
            Assert.Single(results.Where(x => x == OmsJournalWriteResult.Collision));
            Assert.Equal(1, await Count(cs, "oms_aggregates"));
            Assert.Equal(1, await Count(cs, "oms_intents"));
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
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
    public async Task CrashBeforeDispatchCommit_PreservesNotSentWithoutPartialAttempt()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            var journal = new OmsPostgresJournal(ds);
            await journal.PersistAsync(request);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.MarkDispatchStartedAsync(request.Scope, request.CommandId,
                    _ => throw new InvalidOperationException("crash-before-dispatch-commit")));
            Assert.Equal(0, await Count(cs, "oms_attempts"));
            Assert.Equal("PersistedNotSent", await ScalarString(cs,
                "SELECT dispatch_phase FROM oms_commands"));
            Assert.Equal("NotSent", await ScalarString(cs, "SELECT state FROM oms_outbox"));
            Assert.Equal(OmsJournalAttemptResult.Started,
                await new OmsPostgresJournal(ds).MarkDispatchStartedAsync(
                    request.Scope, request.CommandId));
            Assert.Equal(1, await Count(cs, "oms_attempts"));
            Assert.Equal("NeedsRecovery", await ScalarString(cs, "SELECT state FROM oms_outbox"));
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
    public async Task DecimalScaleInInbox_IsDuplicateWithoutRewritingOriginalEvidence()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var original = Event(Scope());
            Assert.Equal(OmsJournalInboxResult.Inserted,
                await journal.PersistInboxAsync(original));
            var stored = await ScalarString(cs,
                "SELECT event_json::text || '|' || encode(payload_sha256,'hex') FROM oms_inbox");
            var replay = original with { Quantity = 2.000m, Price = 60000.000m };
            Assert.Equal(original, replay);

            Assert.Equal(OmsJournalInboxResult.Duplicate,
                await new OmsPostgresJournal(ds).PersistInboxAsync(replay));
            Assert.Equal(1, await Count(cs, "oms_inbox"));
            Assert.Equal(0, await Count(cs, "oms_integrity_incidents"));
            Assert.Equal(stored, await ScalarString(cs,
                "SELECT event_json::text || '|' || encode(payload_sha256,'hex') FROM oms_inbox"));

            Assert.Equal(OmsJournalInboxResult.Collision,
                await journal.PersistInboxAsync(replay with { Price = 60001m }));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
            Assert.Equal(stored, await ScalarString(cs,
                "SELECT event_json::text || '|' || encode(payload_sha256,'hex') FROM oms_inbox"));
        });
    }

    [Theory]
    [InlineData(OrderType.Limit)]
    [InlineData(OrderType.Stop)]
    public async Task DecimalScaleInCommand_IsDuplicateWithoutRewritingOriginalJournal(OrderType type)
    {
        await WithPostgres(async (ds, cs) =>
        {
            var journal = new OmsPostgresJournal(ds);
            var request = Request();
            request = request with
            {
                Payload = request.Payload with
                {
                    OrderType = type, PostOnly = false,
                    LimitPrice = type == OrderType.Limit ? 65000m : null,
                    TriggerPrice = type == OrderType.Stop ? 64000m : null
                }
            };
            Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(request));
            var stored = await ScalarString(cs,
                "SELECT request_json::text || '|' || encode(payload_sha256,'hex') FROM oms_commands");
            var replay = request with
            {
                Payload = request.Payload with
                {
                    Quantity = 1.000m,
                    LimitPrice = type == OrderType.Limit ? 65000.000m : null,
                    TriggerPrice = type == OrderType.Stop ? 64000.000m : null
                }
            };
            Assert.Equal(request, replay);

            Assert.Equal(OmsJournalWriteResult.Duplicate,
                await new OmsPostgresJournal(ds).PersistAsync(replay));
            foreach (var table in new[] { "oms_aggregates", "oms_intents", "oms_commands", "oms_outbox" })
                Assert.Equal(1, await Count(cs, table));
            Assert.Equal(1, await ScalarLong(cs, "SELECT version FROM oms_aggregates"));
            Assert.Equal(0, await Count(cs, "oms_integrity_incidents"));
            Assert.Equal(stored, await ScalarString(cs,
                "SELECT request_json::text || '|' || encode(payload_sha256,'hex') FROM oms_commands"));

            Assert.Equal(OmsJournalWriteResult.Collision,
                await journal.PersistAsync(replay with
                { Payload = replay.Payload with { Quantity = 2m } }));
            Assert.Equal(1, await Count(cs, "oms_integrity_incidents"));
            Assert.Equal(stored, await ScalarString(cs,
                "SELECT request_json::text || '|' || encode(payload_sha256,'hex') FROM oms_commands"));
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
    public async Task OutboxGuard_RefusesRearmAfterCommittedDispatchBoundary()
    {
        await WithPostgres(async (ds, cs) =>
        {
            var request = Request();
            var journal = new OmsPostgresJournal(ds);
            await journal.PersistAsync(request);
            await journal.MarkDispatchStartedAsync(request.Scope, request.CommandId);
            await using var db = new NpgsqlConnection(cs);
            await db.OpenAsync();
            await using var rearm = new NpgsqlCommand(
                "UPDATE oms_outbox SET state='NotSent'", db);
            await Assert.ThrowsAsync<PostgresException>(() => rearm.ExecuteNonQueryAsync());
            Assert.Equal("NeedsRecovery", await ScalarString(cs,
                "SELECT state FROM oms_outbox"));
        });
    }

    [Fact]
    public async Task EmptyJournalMigration_CanBeRolledBackWithoutTouchingLegacyTables()
    {
        await WithPostgres(async (_, cs) =>
        {
            var options = new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql(cs).Options;
            await using var db = new TradeOpsDbContext(options);
            await db.Database.MigrateAsync("20261004190000_AddTradingViewDeliveryHealthState");
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var check = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_class WHERE relname='oms_commands' AND relkind='r')", connection);
            Assert.False((bool)(await check.ExecuteScalarAsync() ?? true));
            await using var legacy = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_class WHERE relname='Orders' AND relkind='r')",
                connection);
            Assert.True((bool)(await legacy.ExecuteScalarAsync() ?? false));
        });
    }

    [Fact]
    public async Task NonemptyJournalMigration_RefusesDestructiveRollback()
    {
        await WithPostgres(async (ds, cs) =>
        {
            await new OmsPostgresJournal(ds).PersistAsync(Request());
            var options = new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql(cs).Options;
            await using var db = new TradeOpsDbContext(options);
            var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.MigrateAsync("20261004190000_AddTradingViewDeliveryHealthState"));
            Assert.Equal("P0001", refusal.SqlState);
            Assert.Contains("Refusing destructive OMS journal rollback", refusal.MessageText);
            Assert.Equal(1, await Count(cs, "oms_commands"));
            Assert.Equal(1, await Count(cs, "oms_outbox"));
        });
    }

    [Fact]
    public async Task RestrictedApplicationRole_CanJournalRecoverAndDeduplicateWithoutSuperuser()
    {
        await WithPostgres(async (_, cs) =>
        {
            // The fixture owns a unique disposable database; no persistent role is used.
            var role = "oms_app_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(cs);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"""
CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}';
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT, INSERT, UPDATE ON
 oms_aggregates, oms_intents, oms_commands, oms_outbox,
 oms_attempts, oms_inbox, oms_integrity_incidents, oms_account_leases TO "{role}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{role}";
""", admin))
                await create.ExecuteNonQueryAsync();
            try
            {
                var application = new NpgsqlConnectionStringBuilder(cs)
                { Username = role, Password = password, Pooling = false };
                await using var source = NpgsqlDataSource.Create(application.ConnectionString);
                await using var db = await source.OpenConnectionAsync();
                await using (var identity = new NpgsqlCommand("""
SELECT count(*) FROM pg_roles WHERE rolname=current_user
 AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
""", db))
                    Assert.Equal(1L, (long)(await identity.ExecuteScalarAsync())!);
                await using (var permission = new NpgsqlCommand(
                    "SELECT has_table_privilege(current_user, 'oms_commands', 'TRUNCATE')", db))
                    Assert.False((bool)(await permission.ExecuteScalarAsync())!);

                var journal = new OmsPostgresJournal(source);
                var request = Request();
                Assert.Equal(OmsJournalWriteResult.Created, await journal.PersistAsync(request));
                Assert.Equal(OmsJournalWriteResult.Duplicate, await journal.PersistAsync(request with
                { Payload = request.Payload with { Quantity = 1.000m, LimitPrice = 65000.000m } }));
                Assert.Equal(OmsJournalWriteResult.Collision, await journal.PersistAsync(request with
                { Payload = request.Payload with { Quantity = 2m } }));
                Assert.Equal(OmsJournalAttemptResult.Started,
                    await journal.MarkDispatchStartedAsync(request.Scope, request.CommandId));
                Assert.True(await journal.RecordOutcomeAsync(request.Scope, request.CommandId,
                    OmsJournalDispatchOutcome.Ambiguous));
                Assert.Equal(OmsJournalAttemptResult.AlreadyStartedOrResolved,
                    await new OmsPostgresJournal(source).MarkDispatchStartedAsync(
                        request.Scope, request.CommandId));
                Assert.True(await journal.RecordOutcomeAsync(request.Scope, request.CommandId,
                    OmsJournalDispatchOutcome.Acknowledged));

                var item = Event(request.Scope);
                Assert.Equal(OmsJournalInboxResult.Inserted, await journal.PersistInboxAsync(item));
                Assert.Equal(OmsJournalInboxResult.Duplicate, await journal.PersistInboxAsync(item with
                { Quantity = 2.000m, Price = 60000.000m }));
                Assert.Equal(OmsJournalInboxResult.Collision, await journal.PersistInboxAsync(item with
                { Price = 60001m }));
                Assert.Equal(1L, await journal.TryAcquireMetadataLeaseAsync(
                    request.Scope, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
                Assert.Null(await journal.TryAcquireMetadataLeaseAsync(
                    request.Scope, Guid.NewGuid(), TimeSpan.FromMinutes(1)));

                await using (var tamper = new NpgsqlCommand(
                    "UPDATE oms_commands SET request_json='{}'::jsonb", db))
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                        tamper.ExecuteNonQueryAsync());
                    Assert.Equal("P0001", refused.SqlState);
                }
                await using (var truncate = new NpgsqlCommand("TRUNCATE oms_commands", db))
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                        truncate.ExecuteNonQueryAsync());
                    Assert.Equal("42501", refused.SqlState);
                }
                Assert.Equal(1, await Count(application.ConnectionString, "oms_commands"));
                Assert.Equal(1, await Count(application.ConnectionString, "oms_attempts"));
                Assert.Equal(2, await Count(application.ConnectionString, "oms_integrity_incidents"));
                Assert.Equal("Acknowledged", await ScalarString(application.ConnectionString,
                    "SELECT dispatch_phase FROM oms_commands"));
                Assert.Equal("Acknowledged", await ScalarString(application.ConnectionString,
                    "SELECT dispatch_phase FROM oms_attempts"));
                Assert.Equal("Resolved", await ScalarString(application.ConnectionString,
                    "SELECT state FROM oms_outbox"));
            }
            finally
            {
                await using var drop = new NpgsqlCommand(
                    $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\";", admin);
                await drop.ExecuteNonQueryAsync();
            }
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
            throw new InvalidOperationException(
                "POSTGRES_ACCEPTANCE_NOT_RUN: TRADEOPS_B2_POSTGRES is missing. " +
                "OMS PostgreSQL tests MUST FAIL without an isolated PostgreSQL 17 connection.");

        var dbName = "b2_journal_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
        var test = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        { Database = dbName, Pooling = false };
        await using var adminDb = new NpgsqlConnection(builder.ConnectionString);
        await adminDb.OpenAsync();
        await using (var version = new NpgsqlCommand(
            "SELECT current_setting('server_version_num')::integer", adminDb))
        {
            var versionNumber = Convert.ToInt32(await version.ExecuteScalarAsync());
            if (versionNumber < 170000 || versionNumber >= 180000)
                throw new InvalidOperationException(
                    $"PostgreSQL 17 required for OMS acceptance; found server_version_num={versionNumber}.");
        }
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
