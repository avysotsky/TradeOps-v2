using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.DeribitPrivateSessionProbe;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.QualificationSupport;

namespace TradeOps.DeribitLiveInboxQualification;

internal sealed record LiveInboxResult(string Status, string Stage, bool? DatabaseUsed,
    DeribitPrivateCaptureDiagnosticSnapshot? Diagnostics = null, long InboxRows = 0, long ObservationRows = 0,
    long IncidentRows = 0, bool LegacyUntouched = true, bool PrivateEventCaptureQualified = false,
    bool IndependentlyVerifiedAccount = false, bool ProductionHierarchyQualified = false,
    bool TradingReady = false, int MutatingCalls = 0);

internal static class LiveInboxQualification
{
    internal const string Purpose = "READ_ONLY_WS_INBOX_QUALIFICATION";
    internal static Task<LiveInboxResult> RunAsync(PrivateSessionProbeInput input, HttpClient http, CancellationToken ct) =>
        RunWithTransportAsync(input, http, null, TimeSpan.FromSeconds(input.ObserveSeconds), ct);

    internal static async Task<LiveInboxResult> RunWithTransportAsync(PrivateSessionProbeInput input, HttpClient http,
        Func<DeribitOptions, DeribitPrivateWebSocketStream>? factory, TimeSpan window, CancellationToken ct)
    {
        if (input.Purpose != Purpose) throw new InvalidOperationException("Explicit isolated inbox purpose required.");
        new PrivateSessionProbeInput { Purpose = "READ_ONLY_WS_QUALIFICATION", ClientId = input.ClientId,
            ClientSecret = input.ClientSecret, ExpectedAccountId = input.ExpectedAccountId,
            AccountType = input.AccountType, ObserveSeconds = input.ObserveSeconds }.Validate();

        await using var database = await OmsPostgresTestDatabase.CreateAsync();
        await using var role = await CaptureRole.CreateAsync(database, ct);
        var scope = PrivateSessionProbe.CreateDiagnosticScope(input.ExpectedAccountId, input.AccountType);
        await using var owner = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, scope, ct);
        await using var coordinator = new OmsPrivateCaptureCoordinator(owner);
        if (await Count(role.Source, "oms_inbox", ct) != 0 || await Count(role.Source, "oms_private_capture_observations", ct) != 0)
            throw new InvalidOperationException("A clean owned clone is required.");

        var catalog = new DeribitInstrumentCatalog(http);
        var futures = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "future", ct);
        var options = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "option", ct);
        var manifest = PrivateSessionProbe.CreateManifest(futures, options);
        var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", manifest);
        DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { futures, options });
        var settings = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2",
            ClientId = input.ClientId, ClientSecret = input.ClientSecret };
        var stream = factory?.Invoke(settings) ?? new(settings, NullLogger<DeribitPrivateWebSocketStream>.Instance);
        var diagnostics = new DeribitPrivateCaptureDiagnostics();
        var session = await coordinator.SwitchAsync(scope, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(window + TimeSpan.FromSeconds(25));
        var run = stream.RunCaptureAsync(coordinator, session, plan, stop.Token, diagnostics);
        Stopwatch? observed = null;
        var completedWindow = false;
        try
        {
            while (!run.IsCompleted && !deadline.IsCancellationRequested)
            {
                var current = diagnostics.Snapshot;
                if (current.Problems > 0) break;
                if (current.SubscriptionsConfirmed == 1) observed ??= Stopwatch.StartNew();
                if (observed?.Elapsed >= window) { completedWindow = true; break; }
                await Task.Delay(10, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); } catch { /* aggregate evidence only */ }
        }
        ct.ThrowIfCancellationRequested();
        var evidence = diagnostics.Snapshot;
        var inbox = await Count(role.Source, "oms_inbox", ct);
        var observations = await Count(role.Source, "oms_private_capture_observations", ct);
        var incidents = await Count(role.Source, "oms_integrity_incidents", ct);
        var legacyUntouched = true;
        foreach (var table in new[] { "\"Orders\"", "\"Fills\"", "oms_commands", "oms_outbox", "oms_aggregates", "oms_intents", "oms_attempts" })
            legacyUntouched &= await Count(database.Source, table, ct) == 0;
        var healthy = completedWindow && run.IsCompletedSuccessfully && evidence.Epochs == 1 &&
            evidence.Authenticated == 1 && evidence.AccountsVerified == 1 && evidence.SubscriptionsConfirmed == 1 &&
            evidence.HeartbeatChallenges > 0 && evidence.HeartbeatReplies == evidence.HeartbeatChallenges &&
            evidence.Problems == 0 && incidents == 0 && legacyUntouched &&
            observations == evidence.CaptureReturns && evidence.CaptureReturns == evidence.NormalizedEvents;
        var captured = healthy && observations > 0 && inbox > 0;
        return new(!healthy ? "LIVE_INBOX_FAILED" : captured ? "LIVE_INBOX_CAPTURE_PASS" : "LIVE_INBOX_CONNECTED_NO_EVENTS",
            "INBOX", true, evidence, inbox, observations, incidents, legacyUntouched, captured);
    }

    private static async Task<long> Count(NpgsqlDataSource source, string table, CancellationToken ct)
    {
        // Table names are closed constants above, never input or native identifiers.
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM " + table, connection);
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }

    internal sealed class CaptureRole : IAsyncDisposable
    {
        private readonly OmsPostgresTestDatabase database;
        private readonly string name = "to_app_" + Guid.NewGuid().ToString("N");
        public NpgsqlDataSource Source { get; private set; } = null!;
        private CaptureRole(OmsPostgresTestDatabase database) => this.database = database;

        public static async Task<CaptureRole> CreateAsync(OmsPostgresTestDatabase database, CancellationToken ct)
        {
            var role = new CaptureRole(database);
            await using var admin = await database.Source.OpenConnectionAsync(ct);
            // Session/generated-role settings only. Never change global/server logging or durability.
            await OmsPostgresTestDatabase.Execute(admin, "SET log_error_verbosity=terse; SET log_min_error_statement=panic; SET log_parameter_max_length_on_error=0; SET log_statement=none");
            await using var tx = await admin.BeginTransactionAsync(ct);
            var password = Guid.NewGuid().ToString("N");
            await using var setup = new NpgsqlCommand($"""
CREATE ROLE "{role.name}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';
COMMENT ON ROLE "{role.name}" IS 'tradeops-tests:role:v1:{database.Name}';
ALTER ROLE "{role.name}" SET log_error_verbosity=terse;
ALTER ROLE "{role.name}" SET log_min_error_statement=panic;
ALTER ROLE "{role.name}" SET log_parameter_max_length_on_error=0;
ALTER ROLE "{role.name}" SET log_statement=none;
GRANT CONNECT ON DATABASE "{database.Name}" TO "{role.name}";
GRANT USAGE ON SCHEMA public TO "{role.name}";
GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO "{role.name}";
GRANT SELECT,INSERT ON oms_integrity_incidents TO "{role.name}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{role.name}";
""", admin, tx);
            await setup.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Username = role.name, Password = password, Pooling = true, MinPoolSize = 0, MaxPoolSize = 8,
                SearchPath = "public", IncludeErrorDetail = false, LogParameters = false,
                Timeout = 10, CommandTimeout = 15, Multiplexing = false };
            role.Source = NpgsqlDataSource.Create(settings.ConnectionString);
            return role;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var admin = await database.Source.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("SELECT shobj_description(oid,'pg_authid') FROM pg_roles WHERE rolname=@role", admin);
            verify.Parameters.AddWithValue("role", name);
            if ((string?)await verify.ExecuteScalarAsync() != "tradeops-tests:role:v1:" + database.Name)
                throw new InvalidOperationException("Generated capture role ownership mismatch.");
            await OmsPostgresTestDatabase.Execute(admin, "SET log_error_verbosity=terse; SET log_min_error_statement=panic; SET log_parameter_max_length_on_error=0; SET log_statement=none");
            await OmsPostgresTestDatabase.Execute(admin, $"DROP OWNED BY \"{name}\"; DROP ROLE \"{name}\"");
        }
    }
}
