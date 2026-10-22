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
    || !rootBuilder.Database.StartsWith("tradeops_ibkr_offline_smoke_", StringComparison.Ordinal))
{
    Console.Error.WriteLine("IBKR OFFLINE POSTGRES SMOKE NOT RUN: dedicated disposable DB name required.");
    return 2;
}

var schema = "ibkr_smoke_" + Guid.NewGuid().ToString("N");
var schemaWasCreated = false;
var failure = false;
var completedCases = 0;

try
{
    await using (var admin = new NpgsqlConnection(rootBuilder.ConnectionString))
    {
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
        await create.ExecuteNonQueryAsync();
        schemaWasCreated = true;
    }

    // Every journal operation is restricted to a freshly created,
    // collision-resistant schema within an explicitly disposable local DB.
    var scopedBuilder = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString)
    {
        SearchPath = schema,
        ApplicationName = "TradeOps.IbkrOfflinePostgresSmoke"
    };

    var root = FindRepositoryRoot();
    var ddlPath = Path.Combine(root,
        "scripts", "ibkr", "to-v2-10-offline-journal-schema.sql");
    var schemaSql = await File.ReadAllTextAsync(ddlPath);
    await using (var connection = new NpgsqlConnection(scopedBuilder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var ddl = new NpgsqlCommand(schemaSql, connection);
        await ddl.ExecuteNonQueryAsync();
    }

    await RunAsync(scopedBuilder.ConnectionString,
        () => completedCases++);

    // Independently validate schema and rows stayed within the smoke schema.
    await using (var connection = new NpgsqlConnection(scopedBuilder.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ibkr_offline_intents", connection);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Check(count == 2, "unexpected offline order count");
        completedCases++;
    }
}
catch
{
    failure = true;
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
        catch
        {
            failure = true;
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
