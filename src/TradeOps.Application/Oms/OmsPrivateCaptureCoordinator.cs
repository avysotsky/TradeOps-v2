using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsPrivateStreamPhase { Disconnected, Connecting, Authenticating, Subscribing, Recovering, Stopped }
public enum OmsPrivateStreamProblem
{
    AwaitingReconciliation, TransportLost, ProtocolViolation, AuthenticationRejected,
    AccountMismatch, BufferOverflow, CaptureFailed, UnsupportedInstrument, Collision, Renewal
}
public sealed record OmsPrivateStreamStatus(AccountKey Scope, Guid Epoch,
    OmsPrivateStreamPhase Phase, OmsPrivateStreamProblem Problem, OmsPrivateStreamProblem? LastFailure = null);

/// <summary>One server-owned account binding. Its token ends the old transport on account switch.</summary>
public sealed class OmsPrivateCaptureSession
{
    private readonly CancellationTokenSource lifetime = new();
    private Runner? currentRunner;
    private long nextRunnerGeneration;
    public AccountKey Scope { get; }
    public CancellationToken Cancellation => lifetime.Token;
    internal bool HasRunner => Volatile.Read(ref currentRunner) is not null;
    internal long RunnerGeneration => Volatile.Read(ref currentRunner)?.Generation ?? 0;
    internal OmsPrivateCaptureSession(AccountKey scope) => Scope = scope;
    internal void Stop() => lifetime.Cancel();

    public IDisposable AcquireRunner()
    {
        Cancellation.ThrowIfCancellationRequested();
        var runner = new Runner(this, Interlocked.Increment(ref nextRunnerGeneration));
        if (Interlocked.CompareExchange(ref currentRunner, runner, null) is not null)
            throw new InvalidOperationException("A capture session already owns a private stream.");
        try { Cancellation.ThrowIfCancellationRequested(); return runner; }
        catch { runner.Dispose(); throw; }
    }
    private sealed class Runner(OmsPrivateCaptureSession session, long generation) : IDisposable
    {
        internal long Generation { get; } = generation;
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.CompareExchange(ref session.currentRunner, null, this);
        }
    }
}

/// <summary>
/// Capture/account/epoch barrier only, not execution fencing or B4 readiness.
/// Switch waits for the previous commit to finish; after it returns stale streams cannot commit.
/// </summary>
public sealed partial class OmsPrivateCaptureCoordinator : IAsyncDisposable
{
    private readonly IOmsPrivateEventCapture capture;
    public OmsPrivateCaptureCoordinator(IOmsPrivateEventCapture capture, OmsPrivateReplayLimits? replayLimits = null, TimeProvider? replayClock = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        replaySettings = replayLimits?.Validate();
        replayTime = replayClock ?? TimeProvider.System;
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private OmsPrivateCaptureSession? active;
    private OmsPrivateStreamStatus? status;
    private Guid epoch;
    private bool stopped;
    public OmsPrivateStreamStatus? Status => Volatile.Read(ref status);

    public async Task<OmsPrivateCaptureSession> SwitchAsync(AccountKey scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Venue.Value != "DERIBIT" || scope.Environment is not (OmsEnvironment.Testnet or OmsEnvironment.Mainnet))
            throw new ArgumentException("A trusted Deribit testnet/mainnet binding is required.");
        OmsPrivateCaptureSession? previous;
        OmsPrivateCaptureSession next;
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                previous = active;
                ResetReplay(OmsPrivateReplayProblem.StaleSession);
                next = active = new(scope);
                epoch = Guid.Empty;
                Volatile.Write(ref status, new(scope, epoch, OmsPrivateStreamPhase.Disconnected,
                    OmsPrivateStreamProblem.AwaitingReconciliation));
            }
        }
        finally { gate.Release(); }
        previous?.Stop(); // cancellation callbacks cannot run while the commit barrier is held
        return next;
    }

    public async Task<Guid> BeginEpochAsync(OmsPrivateCaptureSession session, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                RequireActive(session);
                ResetReplay(OmsPrivateReplayProblem.StaleEpoch);
                epoch = Guid.NewGuid();
                epochRunnerGeneration = session.RunnerGeneration;
                Volatile.Write(ref status, new(session.Scope, epoch, OmsPrivateStreamPhase.Connecting,
                    OmsPrivateStreamProblem.AwaitingReconciliation, status?.LastFailure));
                return epoch;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> UpdateAsync(OmsPrivateCaptureSession session, Guid expectedEpoch,
        OmsPrivateStreamPhase phase, OmsPrivateStreamProblem problem, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(phase) || !Enum.IsDefined(problem)) throw new ArgumentException("Unknown capture status.");
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                if (!IsActive(session) || epoch != expectedEpoch) return false;
                if (replaySettings is not null && (problem != OmsPrivateStreamProblem.AwaitingReconciliation ||
                    phase == OmsPrivateStreamPhase.Stopped || replayWindow is not null && phase != OmsPrivateStreamPhase.Recovering))
                    FailReplay(OmsPrivateReplayProblem.StreamGap);
                Volatile.Write(ref status, new(session.Scope, epoch, phase, problem,
                    problem == OmsPrivateStreamProblem.AwaitingReconciliation ? status?.LastFailure : problem));
                return true;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<OmsPrivateCaptureResult?> TryCaptureAsync(OmsPrivateCaptureSession session, Guid expectedEpoch,
        OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!IsActive(session) || epoch != expectedEpoch) return null;
            if (envelope.Scope != session.Scope || envelope.Observation.StreamEpoch != expectedEpoch)
            {
                if (replaySettings is not null) FailReplay(OmsPrivateReplayProblem.InvalidObservation);
                throw new ArgumentException("Capture scope/epoch does not match the server binding.");
            }
            if (replaySettings is not null) FailReplay(OmsPrivateReplayProblem.UntrackedCapture);
            try
            {
                var result = await capture.CaptureAsync(envelope, ct);
                if (result == OmsPrivateCaptureResult.Collision && replaySettings is not null) FailReplay(OmsPrivateReplayProblem.Collision);
                return result;
            }
            catch { if (replaySettings is not null) FailReplay(OmsPrivateReplayProblem.CaptureFailed); throw; }
        }
        finally { gate.Release(); }
    }

    private bool IsActive(OmsPrivateCaptureSession session) => !stopped && ReferenceEquals(active, session) && !session.Cancellation.IsCancellationRequested;
    private void RequireActive(OmsPrivateCaptureSession session)
    {
        if (!IsActive(session)) throw new OperationCanceledException("Stale private capture binding.");
    }

    public async ValueTask DisposeAsync()
    {
        OmsPrivateCaptureSession? previous;
        await gate.WaitAsync();
        try
        {
            lock (replaySync)
            {
                stopped = true;
                ResetReplay(OmsPrivateReplayProblem.CoordinatorStopped);
                previous = active;
                if (active is not null) Volatile.Write(ref status, new(active.Scope, epoch,
                    OmsPrivateStreamPhase.Stopped, OmsPrivateStreamProblem.AwaitingReconciliation, status?.LastFailure));
            }
        }
        finally { gate.Release(); }
        previous?.Stop();
    }
}
