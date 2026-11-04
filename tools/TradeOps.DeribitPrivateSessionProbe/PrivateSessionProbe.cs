using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;

namespace TradeOps.DeribitPrivateSessionProbe;

// Deliberately not a record: default ToString must never render credentials.
public sealed class PrivateSessionProbeInput
{
    public string Purpose { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public long ExpectedAccountId { get; set; }
    public string AccountType { get; set; } = "";
    public int ObserveSeconds { get; set; } = 45;
    public void Validate()
    {
        if (Purpose != "READ_ONLY_WS_QUALIFICATION" || ExpectedAccountId <= 0 || AccountType is not ("main" or "subaccount") ||
            ObserveSeconds is < 30 or > 60 || string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 512 ||
            string.IsNullOrWhiteSpace(ClientSecret) || ClientSecret.Length > 4096 ||
            ClientId.Any(char.IsControl) || ClientSecret.Any(char.IsControl))
            throw new InvalidOperationException("Invalid read-only session probe input.");
    }
}

public sealed record PrivateSessionProbeResult(string Status, string Stage,
    DeribitPrivateCaptureDiagnosticSnapshot? Diagnostics, int ManifestCount, int UnstoredPrivateEvents,
    bool DatabaseUsed = false, int DurableWrites = 0, bool IndependentlyVerifiedAccount = false,
    bool AccountCoverageQualified = false, bool TradingReady = false, int MutatingCalls = 0);

public static class PrivateSessionProbe
{
    public static async Task<PrivateSessionProbeResult> RunAsync(PrivateSessionProbeInput input, HttpClient http,
        Func<DeribitOptions, DeribitPrivateWebSocketStream>? streamFactory = null,
        CancellationToken ct = default)
    {
        input.Validate();
        var catalog = new DeribitInstrumentCatalog(http);
        var futures = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "future", ct);
        var options = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "option", ct);
        var manifest = CreateManifest(futures, options);
        var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", manifest);
        DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, plan, new[] { futures, options });
        return await ObserveAsync(input, plan, streamFactory, TimeSpan.FromSeconds(input.ObserveSeconds), ct);
    }

    public static IReadOnlyList<DeribitPrivateInstrument> CreateManifest(DeribitInstrumentCatalogSnapshot futures,
        DeribitInstrumentCatalogSnapshot options)
    {
        if (futures.Environment != OmsEnvironment.Mainnet || options.Environment != OmsEnvironment.Mainnet ||
            futures.QueryCurrency != "BTC" || options.QueryCurrency != "BTC" || futures.Kind != "future" || options.Kind != "option")
            throw new InvalidOperationException("Unqualified public instrument groups.");
        var all = futures.Instruments.Concat(options.Instruments).ToArray();
        if (futures.Instruments.Count == 0 || options.Instruments.Count == 0 || all.Length > 4096 ||
            all.Any(x => x.BaseCurrency != "BTC" || x.SettlementCurrency != "BTC" || x.InstrumentType != "reversed" ||
                x.QuoteCurrency != (x.Kind == "future" ? "USD" : "BTC")))
            throw new InvalidOperationException("Unsupported probe instrument coverage.");
        // Explicitly qualified inverse BTC futures/options group only. No symbol/currency inference.
        return Array.AsReadOnly(all.Select(x => new DeribitPrivateInstrument(x.Name, x.Kind, "BTC", x.AmountUnit, x.AmountCurrency)).ToArray());
    }

    internal static async Task<PrivateSessionProbeResult> ObserveAsync(PrivateSessionProbeInput input,
        DeribitPrivateCapturePlan plan, Func<DeribitOptions, DeribitPrivateWebSocketStream>? factory,
        TimeSpan window, CancellationToken ct)
    {
        var diagnostics = new DeribitPrivateCaptureDiagnostics();
        var sink = new RejectingCapturePort();
        await using var coordinator = new OmsPrivateCaptureCoordinator(sink);
        // Diagnostic scope only. Account denotes the authenticated native entity, NOT an inferred parent.
        // A subaccount marker repeats its known native ID; no parent-account mapping is manufactured.
        // This scope is never persisted, promoted or installed as a production account hierarchy.
        var native = "observed-native:" + input.ExpectedAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var scope = new AccountKey(new("local-readonly-qualification"), new("DERIBIT"), OmsEnvironment.Mainnet,
            new(native), input.AccountType == "subaccount" ? new TradingSubaccountId(native) : null);
        var session = await coordinator.SwitchAsync(scope, ct);
        var settings = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2",
            ClientId = input.ClientId, ClientSecret = input.ClientSecret };
        var stream = factory?.Invoke(settings) ?? new(settings, NullLogger<DeribitPrivateWebSocketStream>.Instance);
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
                if (current.Problems > 0 || current.NormalizedEvents > 0) break;
                if (current.SubscriptionsConfirmed == 1) observed ??= Stopwatch.StartNew();
                if (observed?.Elapsed >= window) { completedWindow = true; break; }
                await Task.Delay(10, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Report aggregate typed diagnostics only, never transport/private error payloads. */ }
        }
        ct.ThrowIfCancellationRequested();
        var result = diagnostics.Snapshot;
        var passed = completedWindow && result.Epochs == 1 && result.Authenticated == 1 &&
            result.AccountsVerified == 1 && result.SubscriptionsConfirmed == 1 && result.HeartbeatChallenges > 0 &&
            result.HeartbeatReplies == result.HeartbeatChallenges && result.Problems == 0 && sink.Rejected == 0 &&
            result.CaptureReturns == 0 && result.NormalizedEvents == 0 && run.IsCompleted;
        return new(result.NormalizedEvents > 0 ? "PRIVATE_EVENT_OBSERVED_NOT_STORED" : passed ? "LIVE_SESSION_PASS_NO_DURABLE_CAPTURE" : "LIVE_SESSION_FAIL",
            run.IsCompleted ? "SESSION" : "SESSION_SHUTDOWN", result, plan.InstrumentCount, result.NormalizedEvents);
    }

    private sealed class RejectingCapturePort : IOmsPrivateEventCapture
    {
        private int rejected;
        public int Rejected => Volatile.Read(ref rejected);
        public Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref rejected);
            throw new InvalidOperationException("Protocol-only probe does not persist private events.");
        }
    }
}
