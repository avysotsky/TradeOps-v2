using Npgsql;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;

// This executable is never run by TradeOps.Api or TradeOps.Worker. It
// explicitly refuses remote or non-disposable PostgreSQL targets.
const string confirm = "DISPOSABLE_LOCAL_POSTGRES_ONLY";
var connectionString = Environment.GetEnvironmentVariable("TRADEOPS_IBKR_OFFLINE_SMOKE_PG");
var acknowledged = Environment.GetEnvironmentVariable("TRADEOPS_IBKR_OFFLINE_SMOKE_CONFIRM");

if (acknowledged != confirm || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE NOT RUN: explicit disposable local DB opt-in required.");
    return 2;
}

NpgsqlConnectionStringBuilder rootBuilder;
try
{
    rootBuilder = new NpgsqlConnectionStringBuilder(connectionString);
}
catch
{
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE NOT RUN: invalid connection configuration.");
    return 2;
}

if (!string.Equals(rootBuilder.Host, "localhost", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(rootBuilder.Host, "127.0.0.1", StringComparison.Ordinal)
    && !string.Equals(rootBuilder.Host, "::1", StringComparison.Ordinal))
{
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE NOT RUN: target must be loopback only.");
    return 2;
}

if (string.IsNullOrWhiteSpace(rootBuilder.Database)
    || !rootBuilder.Database.StartsWith("tradeops_ibkr_offline_smoke_", StringComparison.Ordinal)
    || rootBuilder.Database.Length > 63
    || rootBuilder.Database.Length == "tradeops_ibkr_offline_smoke_".Length
    || !rootBuilder.Database.All(c => c is >= 'a' and <= 'z'
        or >= '0' and <= '9' or '_')
    || !string.IsNullOrWhiteSpace(rootBuilder.SearchPath)
    || !string.IsNullOrWhiteSpace(rootBuilder.Options))
{
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE NOT RUN: dedicated disposable DB name required.");
    return 2;
}

var schema = "ibkr_smoke_" + Guid.NewGuid().ToString("N");
var schemaWasCreated = false;
var failure = false;
var completedCases = 0;
var phase = "bootstrap";

try
{
    phase = "create_schema";
    await using (var admin = new NpgsqlConnection(rootBuilder.ConnectionString))
    {
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
        await create.ExecuteNonQueryAsync();
        schemaWasCreated = true;
    }

    phase = "prepare_disposable_schema";
    // Every journal operation is restricted to a freshly created,
    // collision-resistant schema within an explicitly disposable local DB.
    var scopedBuilder = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString)
    {
        SearchPath = schema,
        ApplicationName = "TradeOps.IbkrOfflinePostgresSmoke"
    };

    // Validate the final scoped connection BEFORE any schema DDL runs.
    // A public, aliased or multi-schema search path is never permissible.
    IbkrOfflineJournalConnectionPolicy.Validate(scopedBuilder.ConnectionString);

    var root = FindRepositoryRoot();
    var ddlPath = Path.Combine(root,
        "scripts", "ibkr", "to-v2-10-offline-journal-schema.sql");
    var schemaSql = await File.ReadAllTextAsync(ddlPath);
    var reconciliationDdl = await File.ReadAllTextAsync(Path.Combine(root,
        "scripts", "ibkr", "to-v2-12-offline-reconciliation-schema.sql"));
    await using (var connection = new NpgsqlConnection(scopedBuilder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var ddl = new NpgsqlCommand(schemaSql, connection);
        await ddl.ExecuteNonQueryAsync();
        await using var reconciliationSchema = new NpgsqlCommand(reconciliationDdl, connection);
        await reconciliationSchema.ExecuteNonQueryAsync();
    }

    phase = "journal_transactions_and_concurrency";
    await RunAsync(scopedBuilder.ConnectionString,
        () => completedCases++);
    phase = "reconciliation_and_atomic_rollback";
    await RunReconciliationAsync(scopedBuilder.ConnectionString,
        () => completedCases++);

    phase = "schema_sql_row_verification";
    // Independently validate schema and rows stayed within the smoke schema.
    await using (var connection = new NpgsqlConnection(scopedBuilder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ibkr_offline_intents", connection);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Check(count == 4, "unexpected offline order count");
        completedCases++;
    }
}
catch (Exception exception)
{
    failure = true;
    // Do NOT print exception.Message/ToString: connection strings, SQL
    // statements or synthetic evidence might accidentally appear there.
    var sqlState = exception is PostgresException postgres
        ? postgres.SqlState
        : "none";
    Console.Error.WriteLine(
        $"IBKR OFFLINE POSTGRES SMOKE FAIL phase={phase} type={exception.GetType().Name} sqlstate={sqlState}");
}
finally
{
    if (schemaWasCreated)
    {
        try
        {
            await using var admin = new NpgsqlConnection(rootBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            failure = true;
            var sqlState = exception is PostgresException postgres
                ? postgres.SqlState
                : "none";
            Console.Error.WriteLine(
                $"IBKR OFFLINE POSTGRES SMOKE FAIL phase=cleanup type={exception.GetType().Name} sqlstate={sqlState}");
        }
    }
}

if (failure || !schemaWasCreated)
{
    // Never report sensitive PG connection strings or real account IDs.
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE FAIL (or cleanup incomplete); check isolated test environment.");
    return 1;
}

Console.WriteLine($"IBKR OFFLINE POSTGRES SMOKE PASS cases={completedCases} broker_requests=0 cleanup=true");
return 0;

static async Task RunAsync(string scopedConnectionString, Action casePassed)
{
    const string account = "offline-account-fixture";
    const string broker = "offline-broker-900";
    var coid = "coid-" + Guid.NewGuid().ToString("N");
    var intent = new IbkrOfflineOrderIntent(
        coid, account, "AAPL", OrderSide.Buy, 5m);

    var journal = new IbkrOfflinePostgresJournal(scopedConnectionString);
    var initial = await journal.RegisterAsync(intent);
    Check(initial.Snapshot.State == IbkrOfflineOrderState.Prepared,
        "initial intent");
    casePassed();

    var duplicate = await new IbkrOfflinePostgresJournal(scopedConnectionString)
        .RegisterAsync(intent);
    Check(duplicate.Snapshot.Intent == intent, "idempotent registration");
    await MustRejectAsync(() => journal.RegisterAsync(intent with { Quantity = 6m }));
    casePassed();

    await journal.AppendEventAsync(account, "preflight", Event(
        coid, IbkrOfflineOrderEventType.PreflightApproved));
    await journal.AppendEventAsync(account, "submission-started", Event(
        coid, IbkrOfflineOrderEventType.SubmissionStarted));

    // Separate object models a new program process after a crash.
    var restarted = new IbkrOfflinePostgresJournal(scopedConnectionString);
    var uncertain = await restarted.LoadAsync(account, coid);
    Check(uncertain.Snapshot.State == IbkrOfflineOrderState.AwaitingAcknowledgement,
        "restart recovery must retain awaiting acknowledgement");
    await MustRejectAsync(() => restarted.AppendEventAsync(
        account, "unsafe-repeat", Event(
            coid, IbkrOfflineOrderEventType.SubmissionStarted)));
    casePassed();

    await restarted.AppendEventAsync(account, "broker-ack",
        new IbkrOfflineOrderEvent(coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged,
            BrokerOrderId: broker));

    var first = await restarted.AppendExecutionAsync(
        account, coid, "execution-1", broker, 2m);
    Check(first.Snapshot.State == IbkrOfflineOrderState.PartiallyFilled
        && first.Snapshot.CumulativeFilled == 2m, "first partial fill");

    var repeated = await new IbkrOfflinePostgresJournal(scopedConnectionString)
        .AppendExecutionAsync(account, coid, "execution-1", broker, 2m);
    Check(repeated.Snapshot.CumulativeFilled == 2m
        && repeated.Executions.Count == 1, "replayed execution must count once");
    await MustRejectAsync(() => journal.AppendExecutionAsync(
        account, coid, "execution-1", broker, 3m));
    casePassed();

    await MustRejectAsync(() => restarted.AppendExecutionAsync(
        account, coid, "too-big", broker, 4m));
    var afterRollback = await journal.LoadAsync(account, coid);
    Check(afterRollback.Snapshot.CumulativeFilled == 2m
        && afterRollback.Executions.Count == 1,
        "failed transaction may not create an execution row");
    casePassed();

    // Run two PostgreSQL connections concurrently; the per-order FOR UPDATE
    // lock must make cumulative accounting correct, without lost writes.
    var firstWriter = new IbkrOfflinePostgresJournal(scopedConnectionString);
    var secondWriter = new IbkrOfflinePostgresJournal(scopedConnectionString);
    await Task.WhenAll(
        firstWriter.AppendExecutionAsync(account, coid, "execution-2", broker, 1m),
        secondWriter.AppendExecutionAsync(account, coid, "execution-3", broker, 2m));

    var final = await new IbkrOfflinePostgresJournal(scopedConnectionString)
        .LoadAsync(account, coid);
    Check(final.Snapshot.State == IbkrOfflineOrderState.Filled
        && final.Snapshot.CumulativeFilled == 5m
        && final.Executions.Count == 3, "concurrent writes or durable replay");
    casePassed();

    // Account-scoped execution IDs cannot be reused by another cOID.
    var otherCoid = "coid-" + Guid.NewGuid().ToString("N");
    var secondIntent = intent with { ClientOrderId = otherCoid };
    await journal.RegisterAsync(secondIntent);
    await journal.AppendEventAsync(account, "preflight", Event(
        otherCoid, IbkrOfflineOrderEventType.PreflightApproved));
    await journal.AppendEventAsync(account, "placement", Event(
        otherCoid, IbkrOfflineOrderEventType.SubmissionStarted));
    await journal.AppendEventAsync(account, "ack", new IbkrOfflineOrderEvent(
        otherCoid, IbkrOfflineOrderEventType.BrokerAcknowledged,
        BrokerOrderId: "offline-broker-901"));
    await MustRejectAsync(() => journal.AppendExecutionAsync(
        account, otherCoid, "execution-1", "offline-broker-901", 1m));
    var untouched = await journal.LoadAsync(account, otherCoid);
    Check(untouched.Executions.Count == 0
        && untouched.Snapshot.CumulativeFilled == 0m,
        "execution ID collision must rollback atomically");
    casePassed();

    var restartedAgain = new IbkrOfflinePostgresJournal(scopedConnectionString);
    var latest = await restartedAgain.LoadAsync(account, coid);
    Check(latest.Snapshot.State == IbkrOfflineOrderState.Filled
        && latest.Executions.Count == 3, "durable independent reconstruction");
    casePassed();
}

static async Task RunReconciliationAsync(string connectionString, Action casePassed)
{
    const string account = "offline-account-fixture";
    const string broker = "reconciliation-broker-901";
    var coid = "coid-" + Guid.NewGuid().ToString("N");
    var intent = new IbkrOfflineOrderIntent(
        coid, account, "AAPL", OrderSide.Buy, 5m);
    var journal = new IbkrOfflinePostgresJournal(connectionString);
    await journal.RegisterAsync(intent);
    await journal.AppendEventAsync(account, "preflight", Event(
        coid, IbkrOfflineOrderEventType.PreflightApproved));
    await journal.AppendEventAsync(account, "placement", Event(
        coid, IbkrOfflineOrderEventType.SubmissionStarted));
    await journal.AppendEventAsync(account, "timeout", Event(
        coid, IbkrOfflineOrderEventType.TransportTimedOut));

    // A direct "reconciled filled" append would bypass the evidence+fill
    // atomic transaction and must fail before interacting with the DB.
    await MustRejectAsync(() => journal.AppendEventAsync(
        account, "forged-terminal", new IbkrOfflineOrderEvent(
            coid, IbkrOfflineOrderEventType.ReconciledFilled,
            BrokerOrderId: broker, CumulativeFilled: 5m)));
    await MustRejectAsync(() => journal.AppendEventAsync(
        account, "forged-not-found", Event(
            coid, IbkrOfflineOrderEventType.ReconciliationNotFound)));
    var beforeReconcile = await journal.LoadAsync(account, coid);
    Check(beforeReconcile.Snapshot.State == IbkrOfflineOrderState.Unknown
        && beforeReconcile.Executions.Count == 0,
        "unsupported direct reconciliation events must not modify the journal");
    casePassed();

    var fill1 = new IbkrOfflineExecutionEvidence(
        "reconciliation-exec-1", account, coid, broker, 2m);
    var order = new IbkrOfflineBrokerOrderEvidence(
        account, coid, broker, "AAPL", OrderSide.Buy, 5m, 2m,
        IbkrOfflineBrokerOrderStatus.PartiallyFilled,
        ExecutionsComplete: true,
        Executions: [fill1]);
    var incomplete = new IbkrOfflineEvidenceSnapshot(
        1, account, OrdersComplete: false, Orders: [order]);
    var inconclusive = await journal.AppendReconciliationAsync(account, coid, incomplete);
    Check(inconclusive.Result.Disposition == IbkrOfflineReconciliationDisposition.Inconclusive
        && inconclusive.Result.Order.State == IbkrOfflineOrderState.Unknown,
        "partial account snapshot must not resolve");
    Check((await journal.LoadAsync(account, coid)).Executions.Count == 0,
        "partial snapshot must not import fills");
    casePassed();

    var complete = new IbkrOfflineEvidenceSnapshot(
        2, account, OrdersComplete: true, Orders: [order]);
    var reconciled = await new IbkrOfflinePostgresJournal(connectionString)
        .AppendReconciliationAsync(account, coid, complete);
    Check(reconciled.Result.Disposition == IbkrOfflineReconciliationDisposition.PartiallyFilled
        && reconciled.Result.Order.CumulativeFilled == 2m
        && reconciled.AddedExecutionIds == 1
        && !reconciled.IdempotentReplay, "atomic reconciliation baseline import");

    var afterRestart = await journal.LoadAsync(account, coid);
    Check(afterRestart.Snapshot.State == IbkrOfflineOrderState.PartiallyFilled
        && afterRestart.Snapshot.CumulativeFilled == 2m
        && afterRestart.Executions.Count == 1, "reconciliation survives restart");
    casePassed();

    var again = await new IbkrOfflinePostgresJournal(connectionString)
        .AppendReconciliationAsync(account, coid, complete);
    Check(again.IdempotentReplay
        && again.AddedExecutionIds == 0, "idempotent observation replay");
    await MustRejectAsync(() => journal.AppendReconciliationAsync(
        account, coid, complete with { Sequence = 1 }));
    await MustRejectAsync(() => journal.AppendReconciliationAsync(
        account, coid, complete with { OrdersComplete = false }));
    casePassed();

    var final = await journal.AppendExecutionAsync(
        account, coid, "reconciliation-exec-2", broker, 3m);
    Check(final.Snapshot.State == IbkrOfflineOrderState.Filled
        && final.Snapshot.CumulativeFilled == 5m
        && final.Executions.Count == 2,
        "full fill must count from imported execution-ID baseline");
    casePassed();

    // On the next order, verify a SQL execution ID conflict rolls back
    // BOTH the normalized audit record and the state transition.
    var otherCoid = "coid-" + Guid.NewGuid().ToString("N");
    await journal.RegisterAsync(intent with { ClientOrderId = otherCoid });
    await journal.AppendEventAsync(account, "preflight", Event(
        otherCoid, IbkrOfflineOrderEventType.PreflightApproved));
    await journal.AppendEventAsync(account, "placement", Event(
        otherCoid, IbkrOfflineOrderEventType.SubmissionStarted));
    await journal.AppendEventAsync(account, "timeout", Event(
        otherCoid, IbkrOfflineOrderEventType.TransportTimedOut));
    var otherBroker = "reconciliation-broker-902";
    var conflicting = new IbkrOfflineEvidenceSnapshot(
        1, account, true,
        [new IbkrOfflineBrokerOrderEvidence(
            account, otherCoid, otherBroker, "AAPL", OrderSide.Buy, 5m, 2m,
            IbkrOfflineBrokerOrderStatus.PartiallyFilled, true,
            [new IbkrOfflineExecutionEvidence(
                "reconciliation-exec-1", account, otherCoid, otherBroker, 2m)])]);

    await MustRejectAsync(() => journal.AppendReconciliationAsync(
        account, otherCoid, conflicting));
    var rolledBack = await journal.LoadAsync(account, otherCoid);
    Check(rolledBack.Snapshot.State == IbkrOfflineOrderState.Unknown
        && rolledBack.Executions.Count == 0,
        "failed reconciliation cannot leave imported fill or accepted state");

    // The same sequence can now be used with valid evidence; its failed
    // attempt must not have created a durable observation row.
    var corrected = conflicting with
    {
        Orders = [conflicting.Orders[0] with
        {
            Executions = [new IbkrOfflineExecutionEvidence(
                "reconciliation-exec-3", account, otherCoid, otherBroker, 2m)]
        }]
    };
    var recovered = await journal.AppendReconciliationAsync(
        account, otherCoid, corrected);
    Check(recovered.Result.Disposition == IbkrOfflineReconciliationDisposition.PartiallyFilled
        && recovered.AddedExecutionIds == 1,
        "rollback must also remove the failed observation");
    casePassed();
}

static IbkrOfflineOrderEvent Event(string coid, IbkrOfflineOrderEventType type) =>
    new(coid, type);

static async Task MustRejectAsync<T>(Func<Task<T>> action)
{
    try
    {
        await action();
    }
    catch (Exception exception) when (exception is InvalidOperationException
        or ArgumentException
        or PostgresException)
    {
        return;
    }

    throw new InvalidOperationException("Expected offline journal rejection did not occur.");
}

static void Check(bool condition, string reason)
{
    if (!condition)
    {
        throw new InvalidOperationException(
            "Offline PostgreSQL journal invariant violation: " + reason);
    }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TradeOps.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Repository root not found.");
}
