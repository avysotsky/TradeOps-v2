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
    private int running;
    public AccountKey Scope { get; }
    public CancellationToken Cancellation => lifetime.Token;
    internal OmsPrivateCaptureSession(AccountKey scope) => Scope = scope;
    internal void Stop() => lifetime.Cancel();

    public IDisposable AcquireRunner()
    {
        Cancellation.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("A capture session already owns a private stream.");
        return new Runner(this);
    }
    private sealed class Runner(OmsPrivateCaptureSession session) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Volatile.Write(ref session.running, 0);
        }
    }
}

/// <summary>
/// Capture/account/epoch barrier only, not execution fencing or B4 readiness.
/// Switch waits for the previous commit to finish; after it returns stale streams cannot commit.
/// </summary>
public sealed class OmsPrivateCaptureCoordinator(IOmsPrivateEventCapture capture) : IAsyncDisposable
{
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
            ObjectDisposedException.ThrowIf(stopped, this);
            previous = active;
            next = active = new(scope);
            epoch = Guid.Empty;
            Volatile.Write(ref status, new(scope, epoch, OmsPrivateStreamPhase.Disconnected,
                OmsPrivateStreamProblem.AwaitingReconciliation));
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
            RequireActive(session);
            epoch = Guid.NewGuid();
            Volatile.Write(ref status, new(session.Scope, epoch, OmsPrivateStreamPhase.Connecting,
                OmsPrivateStreamProblem.AwaitingReconciliation, status?.LastFailure));
            return epoch;
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
            if (!IsActive(session) || epoch != expectedEpoch) return false;
            Volatile.Write(ref status, new(session.Scope, epoch, phase, problem,
                problem == OmsPrivateStreamProblem.AwaitingReconciliation ? status?.LastFailure : problem));
            return true;
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
                throw new ArgumentException("Capture scope/epoch does not match the server binding.");
            return await capture.CaptureAsync(envelope, ct);
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
            stopped = true;
            previous = active;
            if (active is not null) Volatile.Write(ref status, new(active.Scope, epoch,
                OmsPrivateStreamPhase.Stopped, OmsPrivateStreamProblem.AwaitingReconciliation, status?.LastFailure));
        }
        finally { gate.Release(); }
        previous?.Stop();
    }
}
