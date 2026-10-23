using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;

// Hard-fail BEFORE connecting if this is not the disposable loopback API
// database from scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh.
const string confirm = "DISPOSABLE_SCOPED_RESERVATION_PG_ONLY";
var connectionString = Environment.GetEnvironmentVariable(
    "TRADEOPS_SYNTHETIC_RESERVATION_PG");
if (Environment.GetEnvironmentVariable(
        "TRADEOPS_SYNTHETIC_RESERVATION_CONFIRM") != confirm
    || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("SCOPED RESERVATION SMOKE NOT RUN: explicit disposable opt-in required.");
    return 2;
}

NpgsqlConnectionStringBuilder builder;
try
{
    builder = new NpgsqlConnectionStringBuilder(connectionString);
}
catch
{
    Console.Error.WriteLine("SCOPED RESERVATION SMOKE NOT RUN: invalid connection settings.");
    return 2;
}

if (!string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
    || string.IsNullOrWhiteSpace(builder.Database)
    || !builder.Database.StartsWith("tradeops_api_smoke_", StringComparison.Ordinal)
    || builder.Database.Length <= "tradeops_api_smoke_".Length
    || builder.Database.Length > 63
    || !builder.Database.All(ch => ch is >= 'a' and <= 'z'
        or >= '0' and <= '9' or '_')
    || !string.IsNullOrWhiteSpace(builder.SearchPath)
    || !string.IsNullOrWhiteSpace(builder.Options))
{
    Console.Error.WriteLine("SCOPED RESERVATION SMOKE NOT RUN: loopback disposable API database required.");
    return 2;
}

var options = new DbContextOptionsBuilder<TradeOpsDbContext>()
    .UseNpgsql(builder.ConnectionString).Options;
var phase = "initial";
long existingLegacyOrderCount = -1;
long existingLegacyFillCount = -1;
long existingLegacyScopeCount = -1;
try
{
    await using (var db = new TradeOpsDbContext(options))
    {
        var migrations = await db.Database.GetAppliedMigrationsAsync();
        Check(migrations.Contains(
            "20261023120000_AddSyntheticScopedIdempotencyReservations"),
            "migration_not_applied");
        Check(await db.SyntheticScopedOrderReservations.CountAsync() == 0,
            "unexpected_existing_order_reservations");
        Check(await db.SyntheticScopedExecutionReservations.CountAsync() == 0,
            "unexpected_existing_execution_reservations");
        existingLegacyOrderCount = await db.Orders.LongCountAsync();
        existingLegacyFillCount = await db.Fills.LongCountAsync();
        existingLegacyScopeCount = await db.BrokerOrderIdentities.LongCountAsync();
        Check(existingLegacyOrderCount == 1,
            "expected_one_disposable_api_order");
        Check(existingLegacyScopeCount == 0,
            "unexpected_legacy_scope_rows");
    }

    const string client = "synthetic-shared-coid";
    const string broker = "synthetic-shared-broker";
    var a = Order("Mock", "synthetic-account-A",
        Guid.NewGuid(), client, broker, "MOCK:BTCUSDT");
    var b = Order("Mock", "synthetic-account-B",
        Guid.NewGuid(), client, broker, "MOCK:BTCUSDT");
    var c = Order("IbkrOffline", "synthetic-account-A",
        Guid.NewGuid(), client, broker, "SYNTHETIC-IBKR-SYMBOL:BTCUSDT");

    phase = "order_cross_account_and_replay";
    foreach (var order in new[] { a, b, c })
    {
        Check((await ReserveOrder(order)).Disposition ==
            SyntheticReservationDisposition.Inserted, "order_new_insert");
        Check((await ReserveOrder(order)).Disposition ==
            SyntheticReservationDisposition.EquivalentReplay, "order_durable_replay");
    }

    Check((await ReserveOrder(a with { RequestedQuantity = 6m })).Disposition
        == SyntheticReservationDisposition.IdentityConflict, "order_terms_conflict");
    Check((await ReserveOrder(a with
    {
        AccountKey = "synthetic-account-C"
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "order_guid_cross_account");
    Check((await ReserveOrder(a with
    {
        LocalOrderId = Guid.NewGuid()
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "order_coid_other_guid");
    Check((await ReserveOrder(a with
    {
        LocalOrderId = Guid.NewGuid(), ClientOrderId = "synthetic-other-client"
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "order_broker_id_other_coid");
    Check((await ReserveOrder(a with
    {
        BrokerOrderId = null
    })).Disposition == SyntheticReservationDisposition.RequiresProof,
        "unbound_order_requires_proof");

    var d = a with
    {
        LocalOrderId = Guid.NewGuid(),
        ClientOrderId = "synthetic-second-client",
        BrokerOrderId = "synthetic-second-broker"
    };
    Check((await ReserveOrder(d)).Disposition ==
        SyntheticReservationDisposition.Inserted, "second_order_insert");

    phase = "order_concurrent_arbitration";
    var e = a with
    {
        LocalOrderId = Guid.NewGuid(),
        ClientOrderId = "synthetic-racing-client",
        BrokerOrderId = "synthetic-racing-broker"
    };
    var racing = await Task.WhenAll(
        ReserveOrder(e), ReserveOrder(e with { RequestedQuantity = 7m }));
    Check(racing.Count(x => x.Disposition ==
        SyntheticReservationDisposition.Inserted) == 1,
        "exactly_one_racing_order_insert");
    Check(racing.Count(x => x.Disposition ==
        SyntheticReservationDisposition.IdentityConflict) == 1,
        "one_racing_payload_conflict");

    var f = a with
    {
        LocalOrderId = Guid.NewGuid(),
        ClientOrderId = "synthetic-identical-race",
        BrokerOrderId = "synthetic-identical-race-broker"
    };
    var sameRace = await Task.WhenAll(ReserveOrder(f), ReserveOrder(f));
    Check(sameRace.Count(x => x.Disposition ==
        SyntheticReservationDisposition.Inserted) == 1,
        "one_identical_order_insert");
    Check(sameRace.Count(x => x.Disposition ==
        SyntheticReservationDisposition.EquivalentReplay) == 1,
        "one_identical_order_replay");

    phase = "execution_cross_account_and_replay";
    var xa = Execution(a, "synthetic-reused-exec");
    var xb = Execution(b, "synthetic-reused-exec");
    var xc = Execution(c, "synthetic-reused-exec");
    foreach (var execution in new[] { xa, xb, xc })
    {
        Check((await ReserveExecution(execution)).Disposition ==
            SyntheticReservationDisposition.Inserted, "execution_cross_account_insert");
        Check((await ReserveExecution(execution)).Disposition ==
            SyntheticReservationDisposition.EquivalentReplay, "execution_durable_replay");
    }

    Check((await ReserveExecution(xa with { Quantity = 4m })).Disposition
        == SyntheticReservationDisposition.IdentityConflict, "execution_payload_conflict");
    Check((await ReserveExecution(xa with
    {
        LocalOrderId = d.LocalOrderId, ClientOrderId = d.ClientOrderId,
        BrokerOrderId = d.BrokerOrderId
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "execution_id_rebound_to_other_order");
    Check((await ReserveExecution(xa with
    {
        BrokerOrderId = "synthetic-wrong-broker"
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "execution_parent_broker_mismatch");
    Check((await ReserveExecution(xa with
    {
        LocalOrderId = Guid.NewGuid()
    })).Disposition == SyntheticReservationDisposition.IdentityConflict,
        "execution_nonexistent_parent");

    phase = "execution_concurrent_arbitration";
    var raceFill = Execution(a, "synthetic-concurrent-exec");
    var twoFills = await Task.WhenAll(
        ReserveExecution(raceFill),
        ReserveExecution(raceFill with { Fee = 0.5m }));
    Check(twoFills.Count(x => x.Disposition ==
        SyntheticReservationDisposition.Inserted) == 1,
        "one_execution_insert");
    Check(twoFills.Count(x => x.Disposition ==
        SyntheticReservationDisposition.IdentityConflict) == 1,
        "one_execution_payload_conflict");

    phase = "database_five_part_fk_direct_probe";
    // Probe PostgreSQL itself, bypassing the journal's parent preflight:
    // an execution using a wrong broker ID MUST fail FK 23503.
    var fkRejected = false;
    await using (var db = new TradeOpsDbContext(options))
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO ""SyntheticScopedExecutionReservations""
                    (""ProviderKey"", ""AccountKey"", ""ExecutionId"",
                     ""ClientOrderId"", ""LocalOrderId"", ""BrokerOrderId"",
                     ""Quantity"", ""Price"", ""Classification"", ""ReservedAt"")
                    VALUES ({a.ProviderKey}, {a.AccountKey},
                            {"synthetic-bad-fk-exec"}, {a.ClientOrderId},
                            {a.LocalOrderId}, {"wrong-broker-binding"},
                            {1m}, {100m}, {"SyntheticOnly"}, now())");
        }
        catch (PostgresException error) when (
            error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            fkRejected = true;
        }
    }
    Check(fkRejected, "postgres_fk_bypass_blocked");

    phase = "durability_counts_and_legacy_protection";
    await using (var db = new TradeOpsDbContext(options))
    {
        Check(await db.SyntheticScopedOrderReservations.CountAsync() == 6,
            "synthetic_order_reservation_count");
        Check(await db.SyntheticScopedExecutionReservations.CountAsync() == 4,
            "synthetic_execution_reservation_count");
        Check(await db.Orders.LongCountAsync() == existingLegacyOrderCount,
            "legacy_order_changed");
        Check(await db.BrokerOrderIdentities.LongCountAsync() == existingLegacyScopeCount,
            "legacy_scope_sidecar_mutated");
        Check(await db.Fills.LongCountAsync() == existingLegacyFillCount,
            "legacy_fills_changed");
    }

    Console.WriteLine("TRADEOPS SCOPED RESERVATION SMOKE PASS orders=6 executions=4 concurrency=PASS scope=PASS replay=PASS conflicts=PASS legacy_unchanged=true broker_requests=0");
    return 0;
}
catch (Exception exception)
{
    // NEVER print connection strings, SQL, credentials or full exception
    // messages. The stage and SQLSTATE are sufficient for initial triage.
    var sqlState = exception is PostgresException sql ? sql.SqlState : "none";
    Console.Error.WriteLine(
        $"SCOPED RESERVATION SMOKE FAIL phase={phase} type={exception.GetType().Name} sqlstate={sqlState}");
    return 1;
}

async Task<SyntheticReservationResult> ReserveOrder(ScopedOrderFingerprint x)
{
    await using var context = new TradeOpsDbContext(options);
    return await new EfSyntheticScopedReservationJournal(context)
        .ReserveOrderAsync(x);
}

async Task<SyntheticReservationResult> ReserveExecution(ScopedExecutionFingerprint x)
{
    await using var context = new TradeOpsDbContext(options);
    return await new EfSyntheticScopedReservationJournal(context)
        .ReserveExecutionAsync(x);
}

static ScopedOrderFingerprint Order(string provider, string account,
    Guid localId, string client, string broker, string instrument) =>
    new(provider, account, localId, client, broker, instrument,
        OrderSide.Buy, OrderType.Limit, 5m, 100m);

static ScopedExecutionFingerprint Execution(
    ScopedOrderFingerprint order, string execId) =>
    new(order.ProviderKey, order.AccountKey, order.LocalOrderId,
        order.ClientOrderId, order.BrokerOrderId!, execId,
        2m, 100m, 0.01m, "USD");

static void Check(bool condition, string stage)
{
    if (!condition)
    {
        throw new InvalidOperationException(stage);
    }
}
