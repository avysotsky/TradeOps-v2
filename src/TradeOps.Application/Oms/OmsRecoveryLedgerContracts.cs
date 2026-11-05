using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsRecoveryDispatchPhase { PersistedNotSent = 1, DispatchStarted, Acknowledged, Rejected, Ambiguous }
public enum OmsRecoveryOutboxState { NotSent = 1, NeedsRecovery, Resolved }
public enum OmsRecoveryLedgerProblem { UnsupportedSchema, InvalidRecord, CapacityExceeded }

/// <summary>Journal v1 evidence, not permission to dispatch or evidence of a venue fill.</summary>
public sealed record OmsRecoveryCommand(OmsJournalCommandRequest Request, long AggregateVersion,
    OmsRecoveryDispatchPhase Phase, OmsRecoveryOutboxState Outbox, int AttemptCount,
    OmsRecoveryDispatchPhase? AttemptPhase)
{
    public void Validate(AccountKey scope)
    {
        Request.Validate();
        if (Request.Scope != scope || AggregateVersion != checked(Request.ExpectedAggregateVersion.Value + 1) ||
            !Enum.IsDefined(Phase) || !Enum.IsDefined(Outbox))
            throw new ArgumentException("Invalid command recovery binding.");
        var unsent = Phase == OmsRecoveryDispatchPhase.PersistedNotSent;
        var expectedOutbox = unsent ? OmsRecoveryOutboxState.NotSent :
            Phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous
                ? OmsRecoveryOutboxState.NeedsRecovery : OmsRecoveryOutboxState.Resolved;
        if (Outbox != expectedOutbox || (unsent ? AttemptCount != 0 || AttemptPhase != null :
                AttemptCount != 1 || AttemptPhase != Phase))
            throw new ArgumentException("Inconsistent command, attempt and outbox.");
    }
}

/// <summary>Commands, attempts, outbox and private facts from ONE database snapshot.</summary>
public sealed record OmsRecoveryLedgerSnapshot(OmsPrivateRecoveryReadResult Private,
    IReadOnlyList<OmsRecoveryCommand> Commands, IReadOnlyList<OmsRecoveryLedgerProblem> Problems,
    IReadOnlyList<DeribitJsonRpcCommandReceipt>? CommandReceipts = null)
{
    public bool IsUsable => Private.IsUsable && Problems.Count == 0;
    public IReadOnlyList<DeribitJsonRpcCommandReceipt> Receipts => CommandReceipts ?? Array.Empty<DeribitJsonRpcCommandReceipt>();
}

public interface IOmsRecoveryLedgerReader
{
    Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000,
        CancellationToken cancellationToken = default);
}

public enum DeribitCommandRecoveryProblem
{
    InstrumentOutsideRecoveryScope, UnresolvedDispatch, PlacementIdentityUnproven, MutationOutcomeUnproven,
    RejectionNotReconciled, BoundOrderEvidenceUnavailable, BoundOrderEvidenceMismatch
}

public sealed record DeribitCommandRecoveryIssue(OmsCommandId CommandId, DeribitCommandRecoveryProblem Problem);
public sealed record DeribitRecoveryLedgerAssessment(DeribitRecoveryGateAssessment Orders,
    int JournalCommands, int UnsentCommands, IReadOnlyList<DeribitCommandRecoveryIssue> CommandIssues,
    int BoundCommands = 0)
{
    public bool AccountReady => false;
    public bool DurableProjectionPublished => false;
}
