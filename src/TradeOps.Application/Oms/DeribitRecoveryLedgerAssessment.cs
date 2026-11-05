using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed partial class DeribitRecoveryEvidenceGate
{
    /// <summary>
    /// Reads the account-wide journal before contacting the venue. Neither an ACK nor a label
    /// is a durable native order binding. Unresolved commands never disappear behind a quiet inbox.
    /// </summary>
    public async Task<DeribitRecoveryLedgerAssessment> AssessWithLedgerAsync(DeribitRecoveryGateRequest request,
        IOmsRecoveryLedgerReader ledgerReader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledgerReader);
        var started = time.GetUtcNow();
        var q = ValidateRequest(request, started);
        ct.ThrowIfCancellationRequested();
        var snapshot = await ledgerReader.ReadAsync(q.Scope, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        var commands = snapshot.Commands.ToArray();
        var ledgerProblems = snapshot.Problems.ToArray();
        var privateSnapshot = snapshot.Private with {
            Observations = snapshot.Private.Observations.ToArray(), Problems = snapshot.Private.Problems.ToArray() };
        var valid = privateSnapshot.IsUsable && ledgerProblems.Length == 0 && commands.Length <= 10000 && privateSnapshot.Scope == q.Scope &&
            privateSnapshot.SnapshotId != Guid.Empty && Utc(privateSnapshot.ReadAtUtc) &&
            privateSnapshot.ReadAtUtc >= started.AddSeconds(-5) && privateSnapshot.ReadAtUtc <= time.GetUtcNow().AddSeconds(5);
        try
        {
            foreach (var command in commands) command.Validate(q.Scope);
            valid &= commands.Select(x => x.Request.CommandId).Distinct().Count() == commands.Length;
            foreach (var group in commands.GroupBy(x => x.Request.LocalOrderId))
                valid &= group.Select(x => x.AggregateVersion).Order().SequenceEqual(
                    Enumerable.Range(1, group.Count()).Select(x => (long)x));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { valid = false; }
        if (!valid)
        {
            // Reuse base validation without allowing any network work for a bad command snapshot.
            privateSnapshot = privateSnapshot with { Problems = [OmsPrivateRecoveryReadProblem.InvalidRecord] };
        }
        var gate = new DeribitRecoveryEvidenceGate(new FrozenLedgerPrivateReader(privateSnapshot), queries, knownReader, time);
        var orders = await gate.AssessAsync(request, ct);
        var issues = new List<DeribitCommandRecoveryIssue>();
        if (valid)
        {
            foreach (var command in commands)
            {
                var payload = command.Request.Payload;
                if (payload.Instrument != q.NativeInstrument)
                    issues.Add(new(command.Request.CommandId, DeribitCommandRecoveryProblem.InstrumentOutsideRecoveryScope));
                if (command.Phase == OmsRecoveryDispatchPhase.PersistedNotSent) continue;
                if (command.Phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous)
                    issues.Add(new(command.Request.CommandId, DeribitCommandRecoveryProblem.UnresolvedDispatch));
                issues.Add(new(command.Request.CommandId, command.Phase == OmsRecoveryDispatchPhase.Rejected
                    ? DeribitCommandRecoveryProblem.RejectionNotReconciled
                    : payload.Kind == OmsJournalCommandKind.Place
                        ? DeribitCommandRecoveryProblem.PlacementIdentityUnproven
                        : DeribitCommandRecoveryProblem.MutationOutcomeUnproven));
            }
        }
        var blockers = orders.Blockers.ToHashSet();
        if (!valid) blockers.Add(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid);
        if (issues.Count != 0) blockers.Add(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved);
        return new(orders with { Blockers = blockers.Order().ToArray() }, valid ? commands.Length : 0,
            valid ? commands.Count(x => x.Phase == OmsRecoveryDispatchPhase.PersistedNotSent) : 0, issues.AsReadOnly());
    }

    private sealed class FrozenLedgerPrivateReader(OmsPrivateRecoveryReadResult snapshot) : IOmsPrivateRecoveryReader
    {
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000,
            CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(snapshot); }
    }
}
