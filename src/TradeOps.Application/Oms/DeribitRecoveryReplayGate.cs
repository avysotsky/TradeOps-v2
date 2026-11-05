namespace TradeOps.Application.Oms;

public sealed record DeribitReplayDrainOptions(TimeSpan? MaximumWait = null, TimeSpan? PollInterval = null)
{
    internal TimeSpan Wait => MaximumWait ?? TimeSpan.FromSeconds(5);
    internal TimeSpan Poll => PollInterval ?? TimeSpan.FromMilliseconds(10);
    internal void Validate()
    {
        if (Wait < TimeSpan.FromMilliseconds(1) || Wait > TimeSpan.FromSeconds(30) ||
            Poll < TimeSpan.FromMilliseconds(1) || Poll > TimeSpan.FromMilliseconds(250) || Poll > Wait)
            throw new ArgumentException("Invalid bounded replay drain options.");
    }
}

/// <summary>Diagnostic assessment only; no reusable cut or private replay payload escapes the owned lifecycle.</summary>
public sealed record DeribitRecoveryReplayAssessment(DeribitRecoveryGateAssessment Evidence, Guid ReplayWindowId,
    bool LocalReplayValidated, int BufferedObservations, int SelectedInstrumentObservations,
    IReadOnlyList<OmsPrivateReplayProblem> ReplayProblems);

public sealed partial class DeribitRecoveryEvidenceGate
{
    public async Task<DeribitRecoveryReplayAssessment> AssessWithReplayAsync(DeribitRecoveryGateRequest request,
        OmsPrivateCaptureCoordinator coordinator, OmsPrivateCaptureSession session,
        DeribitReplayDrainOptions? drainOptions = null, CancellationToken ct = default)
    {
        var q = ValidateRequest(request, time.GetUtcNow());
        ArgumentNullException.ThrowIfNull(coordinator); ArgumentNullException.ThrowIfNull(session);
        var options = drainOptions ?? new(); options.Validate();
        if (q.Scope != session.Scope) throw new ArgumentException("Recovery/capture scope mismatch.");
        ct.ThrowIfCancellationRequested();
        var window = await coordinator.BeginReplayAsync(session, q.Epoch, ct);
        try
        {
            var evidence = await AssessAsync(request, ct);
            var replayProblems = new HashSet<OmsPrivateReplayProblem>();
            var valid = false; var count = 0; var selected = 0;
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(options.Wait);
            try
            {
                var cut = await coordinator.SealReplayAsync(window, drain.Token);
                while (true)
                {
                    var replay = await coordinator.ReadReplayAsync(cut, drain.Token);
                    if (!replay.CanReplay)
                    {
                        if (replay.Problems.Count > 0 && replay.Problems.All(x => x == OmsPrivateReplayProblem.PendingCommits))
                        { await Task.Delay(options.Poll, drain.Token); continue; }
                        replayProblems.UnionWith(replay.Problems); break;
                    }
                    if (replay.Scope != q.Scope || replay.Epoch != q.Epoch || replay.WindowId != window.Id ||
                        replay.AfterReceipt != window.AfterReceipt || replay.ThroughReceipt != cut.ThroughReceipt ||
                        replay.ThroughReceipt < replay.AfterReceipt || replay.Entries.Count != replay.ThroughReceipt-replay.AfterReceipt ||
                        replay.Entries.Where((x, i) => x.ReceiptOrdinal != replay.AfterReceipt+i+1).Any())
                    { replayProblems.Add(OmsPrivateReplayProblem.InvalidObservation); break; }
                    foreach (var entry in replay.Entries)
                    {
                        var envelope = entry.Envelope;
                        try { envelope.Validate(); }
                        catch (ArgumentException) { replayProblems.Add(OmsPrivateReplayProblem.InvalidObservation); break; }
                        if (envelope.Scope != q.Scope || envelope.Observation.StreamEpoch != q.Epoch ||
                            envelope.Observation.Source != OmsPrivateCaptureSource.WebSocket ||
                            entry.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                            (envelope.Facts.NativeInstrument == q.NativeInstrument &&
                                (envelope.Facts.Currency != request.Currency || envelope.Facts.AmountUnit != request.AmountUnit || envelope.Facts.AmountCurrency != request.AmountCurrency)))
                        { replayProblems.Add(OmsPrivateReplayProblem.InvalidObservation); break; }
                    }
                    if (replayProblems.Count != 0) break;
                    // Revalidate the actual opaque token after inspection; never trust a copied CanReplay flag.
                    var final = await coordinator.ReadReplayAsync(cut, drain.Token);
                    if (!final.CanReplay) { replayProblems.UnionWith(final.Problems); break; }
                    valid = true; count = final.Entries.Count;
                    selected = final.Entries.Count(x => x.Envelope.Facts.NativeInstrument == q.NativeInstrument);
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { replayProblems.Add(OmsPrivateReplayProblem.DrainTimeout); }
            ct.ThrowIfCancellationRequested();
            var blockers = evidence.Blockers.ToHashSet();
            if (valid) blockers.Remove(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable);
            else blockers.Add(replayProblems.Contains(OmsPrivateReplayProblem.DrainTimeout)
                ? DeribitRecoveryEvidenceBlocker.BufferedReplayDrainTimedOut : DeribitRecoveryEvidenceBlocker.BufferedReplayInvalid);
            return new(evidence with { AssessedAtUtc = time.GetUtcNow(), Blockers = Array.AsReadOnly(blockers.Order().ToArray()) },
                window.Id, valid, valid ? count : 0, valid ? selected : 0, Array.AsReadOnly(replayProblems.Order().ToArray()));
        }
        finally { coordinator.AbandonReplay(window); }
    }
}
