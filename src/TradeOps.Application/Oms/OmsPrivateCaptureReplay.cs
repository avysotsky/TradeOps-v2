using System.Text;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record OmsPrivateReplayLimits(int MaximumObservations = 1024, int MaximumBytes = 2097152,
    int MaximumPendingReceipts = 4096, TimeSpan? MaximumDuration = null)
{
    internal TimeSpan Duration => MaximumDuration ?? TimeSpan.FromMinutes(2);
    internal OmsPrivateReplayLimits Validate()
    {
        if (MaximumObservations is < 1 or > 10000 || MaximumBytes is < 1024 or > 16777216 ||
            MaximumPendingReceipts is < 1 or > 10000 || Duration < TimeSpan.FromMilliseconds(1) || Duration > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Invalid bounded private replay limits.");
        return this;
    }
}
public enum OmsPrivateReplayProblem
{
    StaleSession, StaleEpoch, StreamGap, CaptureFailed, Collision, BufferOverflow, InvalidObservation,
    UntrackedCapture, ReceiptOrder, PendingCommits, Expired, Superseded, CoordinatorStopped, Abandoned, DrainTimeout
}

/// <summary>Unforgeable process-local receipt, never a database checkpoint or venue sequence.</summary>
public sealed class OmsPrivateCaptureReceipt
{
    internal OmsPrivateCaptureCoordinator Owner { get; }
    internal OmsPrivateCaptureSession Session { get; }
    internal bool Consumed { get; set; }
    public long Ordinal { get; }
    public Guid Epoch { get; }
    public OmsPrivateEventEnvelope Envelope { get; }
    internal OmsPrivateCaptureReceipt(OmsPrivateCaptureCoordinator owner, OmsPrivateCaptureSession session, Guid epoch,
        long ordinal, OmsPrivateEventEnvelope envelope) { Owner = owner; Session = session; Epoch = epoch; Ordinal = ordinal; Envelope = envelope; }
}
public sealed class OmsPrivateReplayWindow
{
    internal OmsPrivateCaptureCoordinator Owner { get; }
    internal OmsPrivateCaptureSession Session { get; }
    internal long Timestamp { get; }
    internal List<OmsPrivateReplayEntry> Entries { get; } = [];
    internal Dictionary<Guid, byte[]> ObservationHashes { get; } = [];
    internal HashSet<OmsPrivateReplayProblem> Problems { get; } = [];
    internal int Bytes { get; set; }
    internal OmsPrivateReplayCut? Cut { get; set; }
    public Guid Id { get; } = Guid.NewGuid();
    public AccountKey Scope => Session.Scope;
    public Guid Epoch { get; }
    public long AfterReceipt { get; }
    public DateTimeOffset BeganAtUtc { get; }
    internal OmsPrivateReplayWindow(OmsPrivateCaptureCoordinator owner, OmsPrivateCaptureSession session, Guid epoch,
        long after, TimeProvider clock) { Owner = owner; Session = session; Epoch = epoch; AfterReceipt = after;
    Timestamp = clock.GetTimestamp(); BeganAtUtc = clock.GetUtcNow(); }
    internal void Fail(OmsPrivateReplayProblem problem) { Problems.Add(problem); Entries.Clear(); ObservationHashes.Clear(); Bytes = 0; }
}
public sealed class OmsPrivateReplayCut
{
    internal OmsPrivateReplayWindow Window { get; }
    public Guid WindowId => Window.Id;
    public AccountKey Scope => Window.Scope;
    public Guid Epoch => Window.Epoch;
    public long ThroughReceipt { get; }
    public DateTimeOffset SealedAtUtc { get; }
    internal OmsPrivateReplayCut(OmsPrivateReplayWindow window, long through, DateTimeOffset sealedAt)
    { Window = window; ThroughReceipt = through; SealedAtUtc = sealedAt; }
}
public sealed record OmsPrivateReplayEntry(long ReceiptOrdinal, OmsPrivateEventEnvelope Envelope, OmsPrivateCaptureResult Disposition);
public sealed record OmsPrivateReplayBatch(AccountKey Scope, Guid Epoch, Guid WindowId, long AfterReceipt, long ThroughReceipt,
    DateTimeOffset BeganAtUtc, DateTimeOffset SealedAtUtc, IReadOnlyList<OmsPrivateReplayEntry> Entries,
    IReadOnlyList<OmsPrivateReplayProblem> Problems)
{
    public bool CanReplay => Problems.Count == 0;
    public bool VenueCoherent => false;
    public bool AccountReady => false;
}

public sealed partial class OmsPrivateCaptureCoordinator
{
    private readonly object replaySync = new();
    private readonly OmsPrivateReplayLimits? replaySettings;
    private readonly TimeProvider replayTime;
    private readonly Dictionary<long, OmsPrivateCaptureReceipt> pendingReceipts = [];
    private OmsPrivateReplayWindow? replayWindow;
    private OmsPrivateReplayProblem? replayFault;
    private long receivedOrdinal, committedOrdinal;
    private long epochRunnerGeneration;
    public bool ReplayTrackingEnabled => replaySettings is not null;

    /// <summary>Release only this assessment's window without waiting for or cancelling a durable capture.</summary>
    public void AbandonReplay(OmsPrivateReplayWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ReferenceEquals(window.Owner, this)) throw new ArgumentException("Foreign replay window.");
        lock (replaySync)
        {
            window.Fail(OmsPrivateReplayProblem.Abandoned);
            if (ReferenceEquals(replayWindow, window)) replayWindow = null;
        }
    }

    private void FailReplay(OmsPrivateReplayProblem problem)
    {
        lock (replaySync)
        {
            replayFault ??= problem;
            replayWindow?.Fail(problem);
            pendingReceipts.Clear(); // Bounded tracking stops; ingress tickets still allow durable capture.
        }
    }
    private void ResetReplay(OmsPrivateReplayProblem problem)
    {
        lock (replaySync)
        {
            replayWindow?.Fail(problem); replayWindow = null; pendingReceipts.Clear(); replayFault = null;
            receivedOrdinal = committedOrdinal = epochRunnerGeneration = 0;
        }
    }
    private void CheckReplay(OmsPrivateReplayWindow window)
    {
        if (stopped) window.Fail(OmsPrivateReplayProblem.CoordinatorStopped);
        if (!IsActive(window.Session) || !window.Session.HasRunner || window.Session.RunnerGeneration != epochRunnerGeneration)
            window.Fail(OmsPrivateReplayProblem.StaleSession);
        if (window.Epoch != epoch) window.Fail(OmsPrivateReplayProblem.StaleEpoch);
        if (!ReferenceEquals(replayWindow, window)) window.Fail(OmsPrivateReplayProblem.Superseded);
        if (replayTime.GetElapsedTime(window.Timestamp) >= replaySettings!.Duration)
            { window.Fail(OmsPrivateReplayProblem.Expired); if (ReferenceEquals(replayWindow, window)) FailReplay(OmsPrivateReplayProblem.Expired); }
        if (replayFault.HasValue) window.Fail(replayFault.Value);
    }

    public Task<OmsPrivateCaptureReceipt> RegisterReceiptAsync(OmsPrivateCaptureSession session, Guid expectedEpoch,
        OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
    {
        if (replaySettings is null) throw new InvalidOperationException("Replay tracking is opt-in.");
        ct.ThrowIfCancellationRequested();
        lock (replaySync)
        {
            RequireActive(session);
            if (epoch == Guid.Empty || epoch != expectedEpoch || !session.HasRunner || session.RunnerGeneration != epochRunnerGeneration)
                throw new InvalidOperationException("Inactive receipt epoch/runner.");
            try
            {
                envelope.Validate();
                if (envelope.Scope != session.Scope || envelope.Observation.StreamEpoch != epoch || envelope.Observation.Source != OmsPrivateCaptureSource.WebSocket)
                    throw new ArgumentException("Private stream receipt binding refused.");
            }
            catch (ArgumentException) { FailReplay(OmsPrivateReplayProblem.InvalidObservation); throw; }
            var receipt = new OmsPrivateCaptureReceipt(this, session, epoch, checked(++receivedOrdinal), envelope);
            if (!replayFault.HasValue)
            {
                if (pendingReceipts.Count >= replaySettings.MaximumPendingReceipts) FailReplay(OmsPrivateReplayProblem.BufferOverflow);
                else pendingReceipts.Add(receipt.Ordinal, receipt);
            }
            return Task.FromResult(receipt);
        }
    }

    public async Task<OmsPrivateCaptureResult?> TryCaptureReceiptAsync(OmsPrivateCaptureReceipt receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ReferenceEquals(receipt.Owner, this)) throw new ArgumentException("Foreign capture receipt.");
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                if (!IsActive(receipt.Session) || receipt.Epoch != epoch || receipt.Session.RunnerGeneration != epochRunnerGeneration || !receipt.Session.HasRunner) return null;
                if (receipt.Consumed) throw new InvalidOperationException("Receipt already attempted.");
                receipt.Consumed = true;
            }
            OmsPrivateCaptureResult result;
            try { result = await capture.CaptureAsync(receipt.Envelope, ct); }
            catch { FailReplay(OmsPrivateReplayProblem.CaptureFailed); throw; }
            lock (replaySync)
            {
                pendingReceipts.Remove(receipt.Ordinal);
                if (result is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate))
                    { FailReplay(result == OmsPrivateCaptureResult.Collision ? OmsPrivateReplayProblem.Collision : OmsPrivateReplayProblem.CaptureFailed); return result; }
                if (!replayFault.HasValue && receipt.Ordinal != committedOrdinal + 1) FailReplay(OmsPrivateReplayProblem.ReceiptOrder);
                if (!replayFault.HasValue) committedOrdinal = receipt.Ordinal;
                if (replayWindow is not null)
                {
                    CheckReplay(replayWindow);
                    if (replayWindow.Problems.Count == 0 && receipt.Ordinal > replayWindow.AfterReceipt &&
                        (replayWindow.Cut is null || receipt.Ordinal <= replayWindow.Cut.ThroughReceipt))
                    {
                        var encoded = OmsPrivateEventCanonical.Encode(receipt.Envelope);
                        var observation = OmsPrivateEventCanonical.EncodeObservation(receipt.Envelope, encoded);
                        var bytes = Encoding.UTF8.GetByteCount(encoded.Json) + Encoding.UTF8.GetByteCount(observation.Json);
                        if (replayWindow.ObservationHashes.TryGetValue(receipt.Envelope.Observation.CaptureId, out var prior) && !prior.SequenceEqual(observation.Sha256))
                            FailReplay(OmsPrivateReplayProblem.Collision);
                        else if (replayWindow.Entries.Count >= replaySettings!.MaximumObservations || replayWindow.Bytes + bytes > replaySettings.MaximumBytes)
                        FailReplay(OmsPrivateReplayProblem.BufferOverflow);
                        else
                        {
                            replayWindow.ObservationHashes[receipt.Envelope.Observation.CaptureId] = observation.Sha256;
                            replayWindow.Entries.Add(new(receipt.Ordinal, receipt.Envelope, result)); replayWindow.Bytes += bytes;
                        }
                    }
                }
                return result;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<OmsPrivateReplayWindow> BeginReplayAsync(OmsPrivateCaptureSession session, Guid expectedEpoch, CancellationToken ct = default)
    {
        if (replaySettings is null) throw new InvalidOperationException("Replay tracking is opt-in.");
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                RequireActive(session);
                if (epoch == Guid.Empty || epoch != expectedEpoch || !session.HasRunner || session.RunnerGeneration != epochRunnerGeneration || status?.Phase != OmsPrivateStreamPhase.Recovering ||
                    status.Problem != OmsPrivateStreamProblem.AwaitingReconciliation || replayFault.HasValue)
                    throw new InvalidOperationException("Healthy subscribed capture epoch required.");
                replayWindow?.Fail(OmsPrivateReplayProblem.Superseded);
                return replayWindow = new(this, session, epoch, committedOrdinal, replayTime);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<OmsPrivateReplayCut> SealReplayAsync(OmsPrivateReplayWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ReferenceEquals(window.Owner, this)) throw new ArgumentException("Foreign replay window.");
        await gate.WaitAsync(ct);
        try { lock (replaySync) { CheckReplay(window); return window.Cut ??= new(window, receivedOrdinal, replayTime.GetUtcNow()); } }
        finally { gate.Release(); }
    }

    public async Task<OmsPrivateReplayBatch> ReadReplayAsync(OmsPrivateReplayCut cut, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cut);
        if (!ReferenceEquals(cut.Window.Owner, this)) throw new ArgumentException("Foreign replay cut.");
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                var window = cut.Window; CheckReplay(window);
                var problems = window.Problems.ToHashSet();
                if (ReferenceEquals(replayWindow, window) && committedOrdinal < cut.ThroughReceipt) problems.Add(OmsPrivateReplayProblem.PendingCommits);
                var entries = problems.Count == 0 ? window.Entries.ToArray() : [];
                return new(window.Scope, window.Epoch, window.Id, window.AfterReceipt, cut.ThroughReceipt, window.BeganAtUtc, cut.SealedAtUtc,
                    Array.AsReadOnly(entries), Array.AsReadOnly(problems.Order().ToArray()));
            }
        }
        finally { gate.Release(); }
    }
}
