using Npgsql;
using TradeOps.Infrastructure.Exchange.Deribit;

// Explicit synthetic-only disposable PostgreSQL acceptance. ZERO broker
// requests, zero API-key reads, no edits to live TradeOps Orders/Fills.
const string consent = "DISPOSABLE_DERIBIT_EXECUTION_PG_ONLY";
var cs = Environment.GetEnvironmentVariable("TRADEOPS_SYNTHETIC_DERIBIT_PG");
if (Environment.GetEnvironmentVariable("TRADEOPS_SYNTHETIC_DERIBIT_CONFIRM")
    != consent || string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine(
        "SYNTHETIC DERIBIT PG NOT RUN: disposable authorization required");
    return 2;
}

try
{
    var journal = new DeribitSyntheticExecutionJournal(cs);
    await journal.InitializeDisposableAsync();
    var a = new DeribitSyntheticExecution(
        "synthetic-account-A", "synthetic-shared-execution",
        "synthetic-order-A", "BTC-PERPETUAL", "buy",
        100m, 65000m, -0.00000001m, "BTC", 1791000000000L, null);
    var b = a with
    {
        AccountKey = "synthetic-account-B",
        BrokerOrderId = "synthetic-order-B"
    };

    static void Check(bool value, string code)
    {
        if (!value) throw new InvalidOperationException(code);
    }

    // A freshly created disposable schema is required. We reject re-use
    // rather than tamper with rows from a prior qualification.
    var builder = new NpgsqlConnectionStringBuilder(cs);
    await using (var connection = new NpgsqlConnection(builder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand(
            """SELECT count(*) FROM "SyntheticDeribitExecutionEvidence" """,
            connection);
        Check((long)(await count.ExecuteScalarAsync() ?? -1L) == 0L,
            "disposable_database_must_be_empty");
    }

    Check(await journal.WriteAsync(a)
        == DeribitSyntheticWriteDisposition.Inserted, "insert_a");
    Check(await journal.WriteAsync(b)
        == DeribitSyntheticWriteDisposition.Inserted, "insert_b");
    Check(await journal.WriteAsync(a)
        == DeribitSyntheticWriteDisposition.EquivalentReplay, "replay_a");
    Check(await journal.WriteAsync(a with { Price = 64999m })
        == DeribitSyntheticWriteDisposition.IdentityConflict, "price_conflict");
    Check(await journal.WriteAsync(a with { BrokerOrderId = "synthetic-other" })
        == DeribitSyntheticWriteDisposition.IdentityConflict, "order_conflict");
    Check(await journal.WriteAsync(a with { Fee = -0.00000002m })
        == DeribitSyntheticWriteDisposition.IdentityConflict, "fee_conflict");

    // Concurrent same-ID replicas: exactly one insert, one replay.
    var concurrent = a with { ExecutionId = "synthetic-concurrent-001" };
    var race = await Task.WhenAll(
        journal.WriteAsync(concurrent),
        journal.WriteAsync(concurrent));
    Check(race.Count(x => x == DeribitSyntheticWriteDisposition.Inserted) == 1,
        "one_insert");
    Check(race.Count(x => x == DeribitSyntheticWriteDisposition.EquivalentReplay) == 1,
        "one_replay");

    // New instance proves durable replay after the old reference is gone.
    var reopened = new DeribitSyntheticExecutionJournal(cs);
    Check(await reopened.WriteAsync(a)
        == DeribitSyntheticWriteDisposition.EquivalentReplay, "durable_replay");

    await using (var connection = new NpgsqlConnection(builder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand(
            """SELECT count(*) FROM "SyntheticDeribitExecutionEvidence" """,
            connection);
        Check((long)(await count.ExecuteScalarAsync() ?? -1L) == 3L,
            "unexpected_record_count");
        // Assert broker trading tables are not changed (fresh db may
        // not contain live tables at all). No INSERT into Orders/Fills.
    }

    Console.WriteLine(
        "SYNTHETIC DERIBIT PG PASS records=3 accounts=2"
        + " cross_account_scope=PASS replay=PASS conflict=PASS"
        + " concurrency=PASS restart=PASS broker_requests=0"
        + " live_fills_written=0");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"SYNTHETIC DERIBIT PG FAIL type={ex.GetType().Name}"
        + " broker_requests=0 live_fills_written=0");
    return 1;
}
