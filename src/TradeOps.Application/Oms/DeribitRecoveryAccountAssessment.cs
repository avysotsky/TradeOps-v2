namespace TradeOps.Application.Oms;

/// <summary>Server-owned expectations from validated metadata; never derive them from returned private facts.</summary>
public sealed record DeribitAccountInstrumentExpectation(string NativeInstrument, string SizeCurrency, string BaseCurrency);
public sealed record DeribitRecoveryAccountRequest(DeribitAccountSnapshotRequest Query,
    IReadOnlyList<DeribitAccountInstrumentExpectation> ExpectedInstruments);
public enum DeribitRecoveryAccountProblem { ReadSkipped, ReadFailed, BindingInvalid, CoverageInvalid, ObservationInvalid, StaleObservation }

/// <summary>Diagnostic counts only. No positions/balances or reusable freshness/replay authority escape the assessment.</summary>
public sealed record DeribitRecoveryAccountAssessment(DeribitRecoveryGateAssessment Evidence,
    DeribitRecoveryReplayAssessment? Replay, bool ScopedAccountObservationsValidated,
    int RequestedInstruments, int AcceptedPositionRows, bool BalanceObserved,
    IReadOnlyList<DeribitRecoveryAccountProblem> AccountProblems)
{
    public bool AccountWideCoverage => false;
    public bool VenueCoherent => false;
    public bool AccountReady => false;
    public bool DurableProjectionPublished => false;
}

public sealed partial class DeribitRecoveryEvidenceGate
{
    public Task<DeribitRecoveryAccountAssessment> AssessAccountAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest account, IDeribitAccountSnapshotReader accountReader, CancellationToken ct = default) =>
        AssessAccountCoreAsync(request, account, accountReader, null, null, null, ct);

    public Task<DeribitRecoveryAccountAssessment> AssessAccountWithReplayAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest account, IDeribitAccountSnapshotReader accountReader,
        OmsPrivateCaptureCoordinator coordinator, OmsPrivateCaptureSession session,
        DeribitReplayDrainOptions? drainOptions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator); ArgumentNullException.ThrowIfNull(session);
        return AssessAccountCoreAsync(request, account, accountReader, coordinator, session, drainOptions, ct);
    }

    private async Task<DeribitRecoveryAccountAssessment> AssessAccountCoreAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest account, IDeribitAccountSnapshotReader accountReader,
        OmsPrivateCaptureCoordinator? coordinator, OmsPrivateCaptureSession? session,
        DeribitReplayDrainOptions? drainOptions, CancellationToken ct)
    {
        var q = ValidateRequest(request, time.GetUtcNow());
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(accountReader);
        var aq = account.Query ?? throw new ArgumentException("Scoped account query required.");
        if (aq.Scope != q.Scope || aq.Epoch != q.Epoch || aq.Currency != request.Currency || aq.Currency is not ("BTC" or "ETH") ||
            aq.Kind is not ("future" or "option") || aq.MaximumPositions is < 1 or > 256 ||
            aq.MaximumObservationSpan < TimeSpan.FromSeconds(1) || aq.MaximumObservationSpan > TimeSpan.FromSeconds(60) ||
            account.ExpectedInstruments is null || account.ExpectedInstruments.Count is < 1 or > 256)
            throw new ArgumentException("Invalid scoped account assessment binding.");
        var expected = account.ExpectedInstruments.Take(257).ToArray();
        if (expected.Length is < 1 or > 256 || expected.Any(x => x is null || string.IsNullOrWhiteSpace(x.NativeInstrument) ||
                x.NativeInstrument.Length > 160 || x.NativeInstrument != x.NativeInstrument.Trim() || x.NativeInstrument.Any(char.IsControl) ||
                x.BaseCurrency != aq.Currency || x.SizeCurrency != (aq.Kind == "future" ? "USD" : aq.Currency)) ||
            expected.Select(x => x.NativeInstrument).Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            !expected.Any(x => x.NativeInstrument == q.NativeInstrument) ||
            (aq.Kind == "future" ? request.AmountUnit != OmsPrivateAmountUnit.Usd || request.AmountCurrency != "USD"
                : request.AmountUnit != OmsPrivateAmountUnit.BaseCoin || request.AmountCurrency != aq.Currency))
            throw new ArgumentException("Invalid trusted account instrument expectations.");
        var manifest = expected.ToDictionary(x => x.NativeInstrument, StringComparer.Ordinal);
        var problems = new HashSet<DeribitRecoveryAccountProblem>();
        DeribitAccountSnapshotResult? observed = null;
        var called = false; var invoked = default(DateTimeOffset); var received = default(DateTimeOffset);
        DeribitRecoveryReplayAssessment? replay = null; DeribitRecoveryGateAssessment evidence;
        if (coordinator is null)
        { evidence = await AssessAsync(request, ct); await Read(evidence, ct); }
        else
        {
            replay = await AssessReplayCoreAsync(request, coordinator, session!, drainOptions, Read, ct);
            evidence = replay.Evidence;
        }
        ct.ThrowIfCancellationRequested();
        // Validate again AFTER replay drain/final cut validation. No saved diagnostics or success flags are accepted.
        var assessed = time.GetUtcNow();
        if (!called) problems.Add(DeribitRecoveryAccountProblem.ReadSkipped);
        else if (observed is not null) Validate(observed, assessed);
        if (replay?.ReplayProblems.Any(x => x is OmsPrivateReplayProblem.StaleSession or
            OmsPrivateReplayProblem.StaleEpoch or OmsPrivateReplayProblem.CoordinatorStopped) == true)
            problems.Add(DeribitRecoveryAccountProblem.BindingInvalid);
        var accepted = called && observed is not null && problems.Count == 0;
        var blockers = evidence.Blockers.ToHashSet();
        blockers.Add(DeribitRecoveryEvidenceBlocker.AccountSnapshotCoherenceUnproven);
        blockers.Add(DeribitRecoveryEvidenceBlocker.AccountWideCoverageUnproven);
        if (!accepted) blockers.Add(DeribitRecoveryEvidenceBlocker.AccountObservationsUnavailable);
        foreach (var problem in problems) blockers.Add(problem switch {
            DeribitRecoveryAccountProblem.ReadSkipped => DeribitRecoveryEvidenceBlocker.AccountReadSkipped,
            DeribitRecoveryAccountProblem.ReadFailed => DeribitRecoveryEvidenceBlocker.AccountReadFailed,
            DeribitRecoveryAccountProblem.BindingInvalid => DeribitRecoveryEvidenceBlocker.AccountBindingInvalid,
            DeribitRecoveryAccountProblem.CoverageInvalid => DeribitRecoveryEvidenceBlocker.AccountCoverageInvalid,
            DeribitRecoveryAccountProblem.StaleObservation => DeribitRecoveryEvidenceBlocker.AccountObservationStale,
            _ => DeribitRecoveryEvidenceBlocker.AccountObservationInvalid });
        var combined = evidence with { AssessedAtUtc = assessed, Blockers = Array.AsReadOnly(blockers.Order().ToArray()) };
        if (replay is not null) replay = replay with { Evidence = combined };
        return new(combined, replay, accepted, expected.Length, accepted ? observed!.Positions.Count : 0, accepted,
            Array.AsReadOnly(problems.Order().ToArray()));

        async Task Read(DeribitRecoveryGateAssessment orders, CancellationToken token)
        {
            if (orders.Blockers.Any(x => x is DeribitRecoveryEvidenceBlocker.SnapshotUnusable or
                DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid or DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid or
                DeribitRecoveryEvidenceBlocker.KnownOrderSelectionInvalid)) return;
            token.ThrowIfCancellationRequested(); called = true; invoked = time.GetUtcNow();
            var raw = await accountReader.ReadAsync(aq, token); received = time.GetUtcNow();
            if (raw is null || raw.RequestedInstruments is null || raw.Positions is null || raw.Problems is null ||
                raw.RequestedInstruments.Count > 256 || raw.Positions.Count > aq.MaximumPositions || raw.Problems.Count > 8)
            { problems.Add(DeribitRecoveryAccountProblem.ObservationInvalid); return; }
            // The port/caller cannot mutate accepted collections while the independent replay drain awaits commits.
            observed = raw with { RequestedInstruments = Array.AsReadOnly(raw.RequestedInstruments.Take(257).ToArray()),
                Positions = Array.AsReadOnly(raw.Positions.Take(aq.MaximumPositions+1).ToArray()),
                Problems = Array.AsReadOnly(raw.Problems.Take(9).ToArray()) };
        }

        void Validate(DeribitAccountSnapshotResult r, DateTimeOffset now)
        {
            if (r.Scope != aq.Scope || r.Epoch != aq.Epoch || r.Currency != aq.Currency || r.Kind != aq.Kind ||
                r.MaximumObservationSpan != aq.MaximumObservationSpan || !Utc(r.StartedAtUtc) || !Utc(r.CompletedAtUtc) ||
                !Utc(invoked) || !Utc(received) || !Utc(now) || r.StartedAtUtc < invoked || r.CompletedAtUtc < r.StartedAtUtc ||
                r.CompletedAtUtc > received || received < invoked || now < received)
                problems.Add(DeribitRecoveryAccountProblem.BindingInvalid);
            if (r.RequestedInstruments.Count != manifest.Count || r.RequestedInstruments.Distinct(StringComparer.Ordinal).Count() != manifest.Count ||
                r.RequestedInstruments.Any(x => x is null || !manifest.ContainsKey(x)) || r.Positions.Count > aq.MaximumPositions)
                problems.Add(DeribitRecoveryAccountProblem.CoverageInvalid);
            if (r.Problems.Any(x => !Enum.IsDefined(x))) problems.Add(DeribitRecoveryAccountProblem.ObservationInvalid);
            if (!r.QueriesCompleted) { problems.Add(DeribitRecoveryAccountProblem.ReadFailed); return; }
            if (r.CompletedAtUtc-r.StartedAtUtc > aq.MaximumObservationSpan || now < r.StartedAtUtc ||
                now-r.StartedAtUtc > aq.MaximumObservationSpan) problems.Add(DeribitRecoveryAccountProblem.StaleObservation);
            var balance = r.Balance!; var positions = r.PositionsWindow!;
            if (balance.Currency != aq.Currency || !Window(balance.Window) || !Window(positions) ||
                positions.RequestedAtUtc < balance.Window.ReceivedAtUtc)
                problems.Add(DeribitRecoveryAccountProblem.ObservationInvalid);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var position in r.Positions)
            {
                if (position is null || position.NativeInstrument is null || !manifest.TryGetValue(position.NativeInstrument, out var terms) ||
                    !seen.Add(position.NativeInstrument)) { problems.Add(DeribitRecoveryAccountProblem.CoverageInvalid); continue; }
                if (position.Kind != aq.Kind || position.SizeCurrency != terms.SizeCurrency || position.BaseCurrency != terms.BaseCurrency ||
                    !Direction(position.Direction, position.NativeSize) || (aq.Kind == "future"
                        ? !position.BaseSize.HasValue || !Direction(position.Direction, position.BaseSize.Value) : position.BaseSize.HasValue))
                    problems.Add(DeribitRecoveryAccountProblem.ObservationInvalid);
            }
            bool Window(DeribitAccountObservationWindow? w) => w is not null && Utc(w.RequestedAtUtc) && Utc(w.ReceivedAtUtc) &&
                w.RequestedAtUtc >= r.StartedAtUtc && w.ReceivedAtUtc >= w.RequestedAtUtc && w.ReceivedAtUtc <= r.CompletedAtUtc;
        }
    }

    private static bool Direction(string direction, decimal size) => direction switch {
        "buy" => size > 0, "sell" => size < 0, "zero" => size == 0, _ => false };
}
