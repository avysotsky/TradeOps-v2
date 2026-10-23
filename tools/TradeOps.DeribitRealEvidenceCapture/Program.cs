using System.Net;
using TradeOps.Infrastructure.Exchange.Deribit;

// One-shot, explicit operator utility. Not registered in API / Worker / DI.
// This tool NEVER initializes, alters or drops a PostgreSQL schema.
// It reads ONE Deribit mainnet endpoint with an owner-held read-only key,
// then writes only to the independently owner-approved evidence schema.

const string consent = "OWNER_APPROVED_DERIBIT_POSITION_EVIDENCE_PG_V1";
if (Environment.GetEnvironmentVariable("TRADEOPS_DERIBIT_REAL_PG_CAPTURE_CONFIRM")
        != consent)
{
    Console.Error.WriteLine(
        "DERIBIT REAL PG CAPTURE NOT RUN: explicit owner approval required");
    return 2;
}

var clientId = Environment.GetEnvironmentVariable(
    "DERIBIT_LIVE_READONLY_CLIENT_ID");
var clientSecret = Environment.GetEnvironmentVariable(
    "DERIBIT_LIVE_READONLY_CLIENT_SECRET");
var connection = Environment.GetEnvironmentVariable(
    "TRADEOPS_DERIBIT_REAL_PG_CONNECTION");
var host = Environment.GetEnvironmentVariable(
    "TRADEOPS_DERIBIT_REAL_PG_APPROVED_HOST");
var database = Environment.GetEnvironmentVariable(
    "TRADEOPS_DERIBIT_REAL_PG_APPROVED_DATABASE");
var user = Environment.GetEnvironmentVariable(
    "TRADEOPS_DERIBIT_REAL_PG_APPROVED_USER");

if (string.IsNullOrWhiteSpace(clientId)
    || string.IsNullOrWhiteSpace(clientSecret)
    || string.IsNullOrWhiteSpace(connection)
    || string.IsNullOrWhiteSpace(host)
    || string.IsNullOrWhiteSpace(database)
    || string.IsNullOrWhiteSpace(user)
    || user == "postgres"
    || database.StartsWith("tradeops_api_smoke_", StringComparison.Ordinal)
    || !int.TryParse(Environment.GetEnvironmentVariable(
        "TRADEOPS_DERIBIT_REAL_PG_APPROVED_PORT"), out var port)
    || !Guid.TryParse(Environment.GetEnvironmentVariable(
        "TRADEOPS_DERIBIT_REAL_PG_BINDING_ID"), out var bindingId)
    || bindingId == Guid.Empty)
{
    Console.Error.WriteLine(
        "DERIBIT REAL PG CAPTURE NOT RUN: approved target or local read-only credentials missing");
    return 2;
}

try
{
    // Validate database identity/config without connecting or fetching.
    var store = new DeribitRealReadOnlyEvidencePgStore(
        connection, host, port, database, user);
    var credentialFingerprint =
        DeribitRealReadOnlyEvidencePgStore.FingerprintCredentialId(clientId);

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    };
    using var http = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    var client = new DeribitLiveReadOnlyClient(
        http, clientId, clientSecret);
    var capture = await client.ReadPositionEvidenceAsync();

    var count = await store.WritePositionSnapshotAsync(
        new DeribitPositionSnapshotCapture(
            Guid.NewGuid(), bindingId, credentialFingerprint,
            capture.RequestStartedAt, capture.ResponseReceivedAt,
            capture.Positions));

    // Never emit account identity, symbol, native size, balance,
    // fingerprint, DB connection string or raw exception messages.
    Console.WriteLine(
        $"DERIBIT REAL POSITION PG PASS positions_saved={count} broker_mutating_calls=0 pg_writes=1 history_complete=false");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(
        $"DERIBIT REAL POSITION PG FAIL type={error.GetType().Name} broker_mutating_calls=0");
    return 1;
}
