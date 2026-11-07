using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

/// <summary>Supervises global capture and recovery after each epoch or explicit refresh request.
/// A saved baseline never re-enables itself after process restart or a capture gap.</summary>
public sealed class DeribitAccountRecoveryWorker(DeribitRecoverySources sources, IDeribitRecoveryBindingSource bindings,
    DeribitRecoveryHostOptions options, DeribitPrivateWebSocketStream stream, IHttpClientFactory httpFactory,
    ILogger<DeribitAccountRecoveryWorker> logger) : BackgroundService, IOmsExecutionRecoverySource
{
    private OmsLiveExecutionBaseline? executionBaseline;
    public OmsLiveExecutionBaseline? Current { get { var value=Volatile.Read(ref executionBaseline); return value?.Qualified.IsCurrent==true ? value : null; } }
    private OmsRuntimeRecoveryGate? runtime;
    private OmsPrivateCaptureCoordinator? coordinator;
    private OmsPrivateCaptureSession? captureSession;
    public (OmsPrivateCaptureCoordinator Coordinator,OmsPrivateCaptureSession Session)? NativeCapture
        => Volatile.Read(ref coordinator) is {} c && Volatile.Read(ref captureSession) is {} s ? (c,s) : null;
    private DeribitRecoveryCoherenceAssessment? lastAssessment;
    private int requested;
    public OmsPrivateStreamStatus? CaptureStatus => Volatile.Read(ref coordinator)?.Status;
    public DeribitRecoveryCoherenceAssessment? LastAssessment => Volatile.Read(ref lastAssessment);
    public void RequestRecovery() { Volatile.Write(ref executionBaseline,null); Interlocked.Exchange(ref requested, 1); }

    public async Task<OmsRecoveredAccountStateV1?> ReadCurrentAsync(CancellationToken ct = default)
    {
        var gate = Volatile.Read(ref runtime);
        if (gate is null || !await gate.CanUseRecoveredStateAsync(ct) || CaptureStatus is not { } status) return null;
        var archive = await new OmsPostgresRecoveryRunStore(sources.Recovery).ReadPublishedAsync(status.Scope, ct);
        return archive is not null && ReferenceEquals(gate, Volatile.Read(ref runtime)) && await gate.CanUseRecoveredStateAsync(ct) ? archive.State : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OmsRecoveryRunToken? current = null; Task? capturing = null;
        var store = new OmsPostgresRecoveryRunStore(sources.Recovery);
        using var stopCapture = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            await OmsPostgresRecoveryRuntimeGuard.ValidateAsync(sources.Recovery, stoppingToken);
            var metadata = await bindings.ReadAsync(stoppingToken); var binding = metadata.Binding;
            await using var owner = await OmsPostgresPrivateCaptureOwner.AcquireAsync(sources.Capture, binding.Scope, stoppingToken);
            await using var active = new OmsPrivateCaptureCoordinator(owner, new());
            Volatile.Write(ref coordinator, active); var session = await active.SwitchAsync(binding.Scope, stoppingToken);
            Volatile.Write(ref captureSession,session);
            using var http = httpFactory.CreateClient("DeribitAccountRecovery");
            var (engine, expected) = Compose(metadata); var metadataAt = DateTimeOffset.UtcNow;
            capturing = stream.RunCaptureAsync(active, session, binding.Plan, stopCapture.Token);
            var attempted = Guid.Empty;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (capturing.IsCompleted) { await capturing; throw new InvalidOperationException("Capture stopped before host shutdown."); }
                var status = active.Status;
                if (current is not null && (status?.Epoch != current.Start.Epoch || status.Phase != OmsPrivateStreamPhase.Recovering ||
                    status.Problem != OmsPrivateStreamProblem.AwaitingReconciliation))
                {
                    Volatile.Write(ref runtime, null); Volatile.Write(ref executionBaseline,null); await store.StopAsync(current, OmsRecoveryStopReason.ConnectionLost, stoppingToken); current = null;
                }
                if (status?.Phase == OmsPrivateStreamPhase.Recovering && status.Problem == OmsPrivateStreamProblem.AwaitingReconciliation &&
                    (status.Epoch != attempted | Interlocked.Exchange(ref requested, 0) != 0))
                {
                    attempted = status.Epoch; Volatile.Write(ref runtime, null); Volatile.Write(ref executionBaseline,null); Volatile.Write(ref lastAssessment, null);
                    if (DateTimeOffset.UtcNow-metadataAt >= TimeSpan.FromMinutes(4))
                    {
                        var refreshed = await bindings.ReadAsync(stoppingToken);
                        if (refreshed.Binding.Scope != binding.Scope || refreshed.Binding.Plan.ExpectedAccountId != binding.Plan.ExpectedAccountId ||
                            refreshed.Binding.Plan.IsSubaccount != binding.Plan.IsSubaccount)
                            throw new InvalidOperationException("Recovery account pin changed.");
                        metadata = refreshed; (engine, expected) = Compose(metadata); metadataAt = DateTimeOffset.UtcNow;
                    }
                    var prior = await store.ReadPublishedAsync(binding.Scope, stoppingToken);
                    var from = prior?.State.NativeThroughUtc ?? options.InitialFromUtc;
                    var through = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var query = new DeribitRecoveryQuery(binding.Scope, status.Epoch, "ETH-PERPETUAL", from, through,
                        TimeSpan.FromMinutes(2), MaximumPagesPerSource: 10, MaximumFacts: 5000);
                    var outcome = await engine.RecoverAndPublishAsync(new(query, "ETH", OmsPrivateAmountUnit.Usd, "USD"),
                        new(new(binding.Scope, status.Epoch, "ETH", "future", TimeSpan.FromSeconds(30)), expected), active, session, ct: stoppingToken);
                    var head = await store.ReadHeadAsync(binding.Scope, stoppingToken);
                    if (head?.Token.Start.RunId != outcome.Document.Start.RunId) throw new InvalidOperationException("Recovery ownership changed.");
                    current = head.Token; Volatile.Write(ref runtime, outcome.RuntimeGate);
                    Volatile.Write(ref executionBaseline,outcome.ExecutionBaseline);
                    Volatile.Write(ref lastAssessment, outcome.Coherence);
                    logger.LogInformation("Deribit recovery completed; qualified baseline available: {Qualified}; blocking reasons: {Count}.",
                        outcome.RuntimeGate is not null, outcome.Coherence.Problems.Count);
                }
                await Task.WhenAny(capturing, Task.Delay(250, stoppingToken));
            }

            (DeribitRecoveryRunEngine, DeribitAccountInstrumentExpectation[]) Compose(DeribitRecoveryRuntimeBinding m)
            {
                var b = m.Binding;
                var accountExpected = m.Manifest.Where(x => x.Currency == "ETH" && x.Kind == "future")
                    .Select(x => new DeribitAccountInstrumentExpectation(x.NativeInstrument, x.PositionSizeCurrency!, x.BaseCurrency!)).Take(257).ToArray();
                if (accountExpected.Length is < 1 or > 256 || !m.Manifest.Any(x => x.NativeInstrument == "ETH-PERPETUAL" && x.AmountUnit == OmsPrivateAmountUnit.Usd))
                    throw new InvalidOperationException("Qualified scoped ETH-future metadata required.");
                return (new(store, new OmsPostgresRecoveryLedgerReader(sources.Recovery, true),
                    new DeribitRecoveryQueries(b, http), new DeribitKnownOrderReader(b, http), new DeribitAccountSnapshotReader(b, http),
                    universeSources: new(new DeribitAccountInventoryReader(b, http), new DeribitCurrencyRecoveryQueries(b, http),
                        m.Manifest, new DeribitRecoveryClockReader(b, http),options.HistoryCoverage)), accountExpected);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { throw new InvalidOperationException("Deribit account recovery stopped; no current recovered baseline is available."); }
        finally
        {
            Volatile.Write(ref captureSession,null);
            Volatile.Write(ref runtime, null); Volatile.Write(ref executionBaseline,null); stopCapture.Cancel();
            if (capturing is not null) { try { await capturing; } catch when (!capturing.IsCompletedSuccessfully) { } }
            if (current is not null)
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await store.StopAsync(current, OmsRecoveryStopReason.ConnectionLost, finish.Token); }
                catch { throw new InvalidOperationException("Recovery runtime stopped, but its durable disconnect could not be confirmed; operator review required."); }
            }
        }
    }
}
