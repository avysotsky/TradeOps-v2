using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using TradeOps.Worker;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryHostPostgresTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("restart")] [InlineData("reconnect")] [InlineData("refresh")]
    public async Task RestrictedRuntimeUsesNativeReadersAndRecoversEntireIntervalAfterReconnectOrRestart(string mode)
    {
        await using var roles = await Roles.Open(); using var venue = new Venue(); var sockets = new ConcurrentQueue<Socket>();
        OmsRecoveredAccountStateV1 first;
        using (var host = Build(roles, venue, sockets))
        {
            await host.StartAsync(); var worker = host.Services.GetRequiredService<DeribitAccountRecoveryWorker>();
            first = await Current(worker); Assert.Equal(2, first.Balances.Count);
            if (mode == "reconnect")
            {
                sockets.Last().Fail(new IOException("synthetic transport gap"));
                var second = await Current(worker, first.Epoch);
                Assert.Equal(first.NativeThroughUtc, second.RequiredFromUtc); Assert.NotEqual(first.RunId, second.RunId);
            }
            if (mode == "refresh")
            {
                worker.RequestRecovery(); var refreshed = await Current(worker, previousRun: first.RunId);
                Assert.Equal(first.Epoch, refreshed.Epoch); Assert.Equal(first.NativeThroughUtc, refreshed.RequiredFromUtc);
            }
            await host.StopAsync(); Assert.Null(await worker.ReadCurrentAsync());
        }
        if (mode == "restart")
        {
            using var restarted = Build(roles, venue, sockets); await restarted.StartAsync();
            var current = await Current(restarted.Services.GetRequiredService<DeribitAccountRecoveryWorker>(), first.Epoch);
            Assert.Equal(first.NativeThroughUtc, current.RequiredFromUtc); Assert.NotEqual(first.RunId, current.RunId);
            await restarted.StopAsync();
        }
        Assert.Equal(OmsRecoveryRunState.Disconnected, (await new OmsPostgresRecoveryRunStore(roles.Db.Source).ReadHeadAsync(Scope()))!.State);
        Assert.True(mode == "refresh" ? sockets.Count == 1 : sockets.Count >= 2); Assert.All(sockets, s => Assert.All(s.Sent, x => Assert.Contains(x.Method,
            new[] { "public/auth", "private/get_account_summary", "public/set_heartbeat", "private/subscribe", "public/test" })));
        Assert.DoesNotContain(venue.Methods, x => x.Contains("buy") || x.Contains("cancel") || x.Contains("sell"));
    }

    [Fact]
    public async Task UnknownNativePositionPersistsBlockAndDoesNotRepeatSameFailedRecoveryAutomatically()
    {
        await using var roles = await Roles.Open(); using var venue = new Venue { UnknownPosition = true };
        var sockets = new ConcurrentQueue<Socket>(); using var host = Build(roles, venue, sockets); await host.StartAsync();
        var worker = host.Services.GetRequiredService<DeribitAccountRecoveryWorker>(); worker.RequestRecovery();
        var store = new OmsPostgresRecoveryRunStore(roles.Db.Source);
        using var deadline = new CancellationTokenSource(Limit); OmsRecoveryRunHead? head;
        while ((head = await store.ReadHeadAsync(Scope(), deadline.Token))?.State != OmsRecoveryRunState.Blocked) await Task.Delay(10, deadline.Token);
        Assert.Null(await worker.ReadCurrentAsync()); var saved = (await store.ReadResultAsync(Scope(), head.Token.Start.RunId))!;
        Assert.Contains(saved.Universe!.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.UnmappedInstrument);
        var count = venue.Methods.Count; await Task.Delay(600, deadline.Token); Assert.Equal(count, venue.Methods.Count);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("admin")] [InlineData("missing-grant")] [InlineData("sender-grant")]
    public async Task UnsafeOrIncompleteRecoveryRoleRefusesBeforeAnyVenueCall(string mode)
    {
        await using var roles = await Roles.Open();
        if (mode != "admin")
        {
            await using var modify = roles.Db.Source.CreateCommand(mode == "missing-grant"
                ? $"REVOKE EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text) FROM \"{roles.RecoveryName}\""
                : $"GRANT INSERT ON oms_commands TO \"{roles.RecoveryName}\"");
            await modify.ExecuteNonQueryAsync();
        }
        using var venue = new Venue(); using var host = Build(roles, venue, new(), mode == "admin" ? roles.Db.ConnectionString : null);
        await host.StartAsync(); var worker = host.Services.GetRequiredService<DeribitAccountRecoveryWorker>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(Limit));
        Assert.Null(failure.InnerException); Assert.Empty(venue.Methods); Assert.Null(await worker.ReadCurrentAsync()); await host.StopAsync();
    }

    private static async Task<OmsRecoveredAccountStateV1> Current(DeribitAccountRecoveryWorker worker, Guid? previousEpoch = null, Guid? previousRun = null)
    {
        using var stop = new CancellationTokenSource(Limit);
        while (true)
        {
            if (worker.ExecuteTask?.IsFaulted == true) await worker.ExecuteTask;
            if (worker.LastAssessment is { } assessment) Assert.True(assessment.EvidenceSufficient, string.Join(",", assessment.Problems));
            var current = await worker.ReadCurrentAsync(stop.Token);
            if (current is not null && current.Epoch != previousEpoch && current.RunId != previousRun) return current;
            await Task.Delay(10, stop.Token);
        }
    }
    private static IHost Build(Roles roles, Venue venue, ConcurrentQueue<Socket> sockets, string? recovery = null)
    {
        var data = DeribitRecoveryHostStartupTests.Configuration(); data["ConnectionStrings:TradeOpsCaptureDb"] = roles.CaptureConnection;
        data["ConnectionStrings:TradeOpsRecoveryDb"] = recovery ?? roles.RecoveryConnection;
        var builder = DeribitCaptureHostTests.Builder(data); Assert.True(DeribitRecoveryStartup.TryConfigure(builder));
        builder.Services.RemoveAll<IHttpClientFactory>(); builder.Services.AddSingleton<IHttpClientFactory>(new Clients(venue));
        builder.Services.Replace(ServiceDescriptor.Singleton(Stream(() => { var socket = new Socket(); sockets.Enqueue(socket); return socket; })));
        return builder.Build();
    }
    private sealed class Clients(Venue venue) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(venue, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) }; }

    private sealed class Venue : HttpMessageHandler
    {
        internal bool UnknownPosition; internal readonly ConcurrentQueue<string> Methods = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("test.deribit.com", request.RequestUri!.Host); object result; long id = 0;
            if (request.Method == HttpMethod.Get)
            {
                Methods.Enqueue("public/get_instruments"); var args = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
                var currency = args["currency"]; var kind = args["kind"];
                result = kind == "future" && currency is "ETH" or "BTC" ? new object[] { new {
                    instrument_name = currency + "-PERPETUAL", kind = "future", instrument_type = "reversed", base_currency = currency,
                    quote_currency = "USD", settlement_currency = currency, is_active = true } } : Array.Empty<object>();
            }
            else
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = doc.RootElement;
                var method = root.GetProperty("method").GetString()!; id = root.GetProperty("id").GetInt64(); var p = root.GetProperty("params"); Methods.Enqueue(method);
                result = method switch {
                    "public/auth" => new { access_token = "synthetic-token", token_type = "bearer", expires_in = 300, scope = p.GetProperty("scope").GetString() },
                    "public/get_time" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "private/get_account_summary" => new { id = 123, type = "main", currency = p.GetProperty("currency").GetString(), balance = 0, equity = 0,
                        cross_collateral_enabled = false, portfolio_margining_enabled = false },
                    "private/get_account_summaries" => new { id = 123, type = "main", summaries = new[] { "ETH", "BTC" }.Select(x => new {
                        currency = x, balance = 0, equity = 0, cross_collateral_enabled = false, portfolio_margining_enabled = false }).ToArray() },
                    "private/get_positions" => UnknownPosition && p.GetProperty("currency").GetString() == "any"
                        ? new object[] { new { user_id = 123, instrument_name = "UNKNOWN-PRODUCT", kind = "future_combo", direction = "buy", size = 1, size_currency = 0.001m } } : Array.Empty<object>(),
                    "private/get_open_orders" or "private/get_open_orders_by_currency" or "private/get_open_orders_by_instrument" or
                    "private/get_order_history_by_currency" or "private/get_order_history_by_instrument" => Array.Empty<object>(),
                    "private/get_user_trades_by_currency" or "private/get_user_trades_by_instrument" => new { trades = Array.Empty<object>(), has_more = false },
                    _ => throw new InvalidOperationException("Unexpected or mutating native call.") };
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Roles : IAsyncDisposable
    {
        internal OmsPostgresTestDatabase Db = null!;
        internal readonly string CaptureName = "to_capture_" + Guid.NewGuid().ToString("N"), RecoveryName = "to_recovery_" + Guid.NewGuid().ToString("N");
        internal string CaptureConnection = "", RecoveryConnection = "";
        internal static async Task<Roles> Open()
        {
            var r = new Roles { Db = await OmsPostgresTestDatabase.CreateAsync() }; var password = Guid.NewGuid().ToString("N");
            await using var provision = r.Db.Source.CreateCommand($"""
CREATE ROLE "{r.CaptureName}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';
CREATE ROLE "{r.RecoveryName}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}';
COMMENT ON ROLE "{r.CaptureName}" IS 'tradeops-tests:role:v1:{r.Db.Name}';
COMMENT ON ROLE "{r.RecoveryName}" IS 'tradeops-tests:role:v1:{r.Db.Name}';
GRANT CONNECT ON DATABASE "{r.Db.Name}" TO "{r.CaptureName}","{r.RecoveryName}";
GRANT USAGE ON SCHEMA public TO "{r.CaptureName}","{r.RecoveryName}";
GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO "{r.CaptureName}";
GRANT SELECT,INSERT ON oms_integrity_incidents TO "{r.CaptureName}";
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO "{r.CaptureName}";
GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{r.RecoveryName}";
GRANT UPDATE ON oms_commands,oms_attempts,oms_outbox TO "{r.RecoveryName}";
GRANT INSERT ON oms_recovery_runs_v1,oms_recovery_heads_v1,oms_recovery_events_v1,oms_deribit_recovery_results_v1,oms_recovered_account_states_v1 TO "{r.RecoveryName}";
GRANT UPDATE ON oms_recovery_heads_v1 TO "{r.RecoveryName}";
GRANT INSERT (tenant_id,venue_id,environment,account_id,subaccount_id) ON oms_evidence_versions_v1 TO "{r.RecoveryName}";
GRANT USAGE ON SEQUENCE oms_recovery_events_v1_event_id_seq TO "{r.RecoveryName}";
GRANT EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text) TO "{r.RecoveryName}";
""");
            await provision.ExecuteNonQueryAsync();
            r.CaptureConnection = new NpgsqlConnectionStringBuilder(r.Db.ConnectionString) { Username = r.CaptureName, Password = password, Pooling = false }.ConnectionString;
            r.RecoveryConnection = new NpgsqlConnectionStringBuilder(r.Db.ConnectionString) { Username = r.RecoveryName, Password = password, Pooling = false }.ConnectionString;
            return r;
        }
        public async ValueTask DisposeAsync()
        {
            await using (var cleanup = Db.Source.CreateCommand($"DROP OWNED BY \"{CaptureName}\",\"{RecoveryName}\"; DROP ROLE \"{CaptureName}\",\"{RecoveryName}\"")) await cleanup.ExecuteNonQueryAsync();
            await Db.DisposeAsync();
        }
    }
}
