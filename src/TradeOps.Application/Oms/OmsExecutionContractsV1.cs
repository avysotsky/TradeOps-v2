using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record OmsLiveExecutionBaseline(OmsRecoveryPublication Publication, OmsQualifiedRecovery Qualified);
public interface IOmsExecutionRecoverySource
{
    OmsLiveExecutionBaseline? Current { get; }
    void RequestRecovery();
}
public enum OmsExecutionResultKind { Acknowledged = 1, Denied, Unknown }
public sealed record OmsExecutionResult(OmsExecutionResultKind Kind, OmsRiskAdmissionResult? Admission, string Reason);
public interface IOmsOrderIntentGateway
{
    Task<OmsExecutionResult> SubmitAsync(OmsJournalCommandRequest command, CancellationToken ct = default);
}
public interface IOmsAccountOrderTransport : IAsyncDisposable
{
    Task<DeribitJsonRpcCommandReceipt> SendAsync(OmsJournalCommandRequest command, OmsPhysicalWritePermit permit, CancellationToken ct);
    void Abort();
}

/// <summary>Nonserializable, one-use capability. Only a live qualified baseline can mint it.
/// The gateway owns its lifetime and the OS fence; a saved admission/boolean cannot mint one.</summary>
public sealed class OmsPhysicalWritePermit
{
    private readonly OmsQualifiedRecovery recovery;
    private readonly byte[] commandHash;
    private readonly Func<bool> isOwner;
    private int consumed;
    private readonly TaskCompletionSource initiated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task PhysicalIoInitiated => initiated.Task;
    internal OmsPhysicalWritePermit(OmsQualifiedRecovery recovery, OmsJournalCommandRequest command, Func<bool> isOwner)
    { this.recovery = recovery; commandHash = OmsJournalCanonical.EncodeCommand(command).Sha256; this.isOwner = isOwner; }
    public Task InitiateAsync(OmsJournalCommandRequest command, Func<Task> physicalSocketWrite)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !commandHash.SequenceEqual(OmsJournalCanonical.EncodeCommand(command).Sha256))
            throw new InvalidOperationException("Physical write permit already used or terms changed.");
        return recovery.InitiatePhysicalWrite(isOwner, () =>
        {
            // Signal only after the actual socket operation has been invoked, under the
            // same revision/owner barrier. Merely entering a queued sender is insufficient.
            var operation = physicalSocketWrite();
            initiated.TrySetResult();
            return operation;
        });
    }
}
