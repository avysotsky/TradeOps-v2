using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitTerminalEvidenceTests
{
    internal static readonly DateTimeOffset Now = Through.AddHours(2);
    internal static OmsPrivateRecoveryReadResult Snapshot(params OmsPrivateEventEnvelope[] events) => new(Account, Guid.NewGuid(), Now.AddSeconds(-2),
        events.Select(x => { var c = OmsPrivateEventCanonical.Encode(x); return (c.Namespace, c.EventId); }).Distinct().Count(),
        events.Select(x => new OmsPrivateRecoveryObservation(x, OmsPrivateCaptureResult.Inserted)).ToArray(), Array.Empty<OmsPrivateRecoveryReadProblem>());
    internal static DeribitRecoveryQueryResult Batch(params OmsPrivateEventEnvelope[] events) => new(Account, Epoch, "ETH-PERPETUAL", "ETH",
        From.AddHours(-1), Now.AddSeconds(-5), Now.AddSeconds(-1), Now, new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
            .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), events, Array.Empty<DeribitRecoveryQueryProblem>());
    private static OmsPrivateEventEnvelope Current(OmsPrivateEventEnvelope e) => e with { Observation =
        new(Guid.NewGuid(), Epoch, Now, OmsPrivateCaptureSource.RecoveryQuery, e.Facts.Kind == OmsJournalEventKind.Execution
            ? "private/get_user_trades_by_instrument" : "private/get_order_history_by_instrument") };
    internal static DeribitRetainedTerminalAssessment Assess(OmsPrivateRecoveryReadResult s, DeribitRecoveryQueryResult? b = null,
        DeribitKnownOrderReadRequest? known = null) => DeribitRetainedTerminalEvidence.Assess(s,
            known ?? DeribitKnownOrderSelection.FromSnapshot(s, Account, Epoch, "ETH-PERPETUAL"), b ?? Batch(), Now);

    [Theory]
    [InlineData("cancelled", 0)] [InlineData("rejected", 0)] [InlineData("cancelled", 4)] [InlineData("filled", 10)]
    public void ExplicitTerminalCumulativeAndMatchingLedgerAreSufficient(string state, decimal filled)
    {
        var report = Report(state, filled); var prior = Prior();
        var s = filled == 0 ? Snapshot(prior, report) : Snapshot(prior, report, Fill(filled));
        var result = Assess(s); var order = Assert.Single(result.Orders);
        Assert.True(order.IsSufficient); Assert.Empty(order.Problems); Assert.Equal(filled == 0 ? 0 : 1, order.DistinctExecutions);
        Assert.Equal(s.SnapshotId, result.SnapshotId); Assert.Equal(Account, result.Scope); Assert.Equal(Epoch, result.Epoch);
    }

    [Fact]
    public void CumulativeIsNotTurnedIntoInventedExecutions()
    {
        var result = Assert.Single(Assess(Snapshot(Report(filled: 4))).Orders);
        Assert.False(result.IsSufficient); Assert.Equal(0, result.DistinctExecutions);
        Assert.Contains(DeribitTerminalEvidenceProblem.LedgerMismatch, result.Problems);
    }

    [Fact]
    public void RepeatedObservationsAndOverlappingCurrentTradesCountOnlyOnce()
    {
        var report = Report(filled: 4); var fill = Fill();
        var s = Snapshot(Prior(), report, report with { Observation = report.Observation with { CaptureId = Guid.NewGuid() } }, fill, fill);
        var result = Assert.Single(Assess(s, Batch(Current(fill))).Orders);
        Assert.True(result.IsSufficient); Assert.Equal(1, result.DistinctExecutions);
    }

    [Fact]
    public void NewExecutionCannotBeHiddenBehindStoredZeroFillTerminal()
    {
        var result = Assert.Single(Assess(Snapshot(Report()), Batch(Current(Fill()))).Orders);
        Assert.False(result.IsSufficient); Assert.Contains(DeribitTerminalEvidenceProblem.LedgerMismatch, result.Problems);
    }

    [Fact]
    public void CurrentTradeFactsCanCompleteRetainedTerminalQuantityWithoutInventingFills()
    {
        var result = Assert.Single(Assess(Snapshot(Report(filled: 4)), Batch(Current(Fill()))).Orders);
        Assert.True(result.IsSufficient); Assert.Equal(1, result.DistinctExecutions);
    }

    [Fact]
    public void InputOrderDoesNotChangeAValidProof()
    {
        var events = new[] { Prior(), Report(filled: 4), Fill() };
        var first = Assert.Single(Assess(Snapshot(events)).Orders); var reversed = Assert.Single(Assess(Snapshot(events.Reverse().ToArray())).Orders);
        Assert.True(first.IsSufficient); Assert.True(reversed.IsSufficient); Assert.Equal(first.DistinctExecutions, reversed.DistinctExecutions);
    }

    [Fact]
    public void SameTradeIdOnDifferentOwnOrderLegsIsNotCollapsed()
    {
        var a = Report(filled: 4); var fill = Fill();
        var s = Snapshot(a, fill, a with { Facts = a.Facts with { ExchangeOrderId = "order-2" } },
            fill with { Facts = fill.Facts with { ExchangeOrderId = "order-2" } });
        var result = Assess(s); Assert.Equal(2, result.Orders.Count);
        Assert.All(result.Orders, x => { Assert.True(x.IsSufficient); Assert.Equal(1, x.DistinctExecutions); });
    }

    [Theory]
    [InlineData("scope")] [InlineData("epoch")] [InlineData("snapshot-id")] [InlineData("batch-scope")]
    [InlineData("query-window")] [InlineData("query-problem")] [InlineData("sources")]
    public void ChangedBindingCannotQualifyKnownOrder(string mode)
    {
        var s = Snapshot(Report()); var known = DeribitKnownOrderSelection.FromSnapshot(s, Account, Epoch, "ETH-PERPETUAL"); var b = Batch();
        var foreign = new TradeOps.Domain.Oms.AccountKey(new("foreign"), Account.Venue, Account.Environment, Account.Account);
        switch (mode)
        {
            case "scope": known = known with { Scope = foreign }; break;
            case "epoch": known = known with { Epoch = Guid.NewGuid() }; break;
            case "snapshot-id": known = known with { SnapshotId = Guid.NewGuid() }; break;
            case "batch-scope": b = b with { Scope = foreign }; break;
            case "query-window": b = b with { ThroughUtc = b.QueryFromUtc.AddSeconds(-1) }; break;
            case "query-problem": b = b with { Problems = new[] { DeribitRecoveryQueryProblem.Capacity } }; break;
            case "sources": b = b with { Sources = Enumerable.Repeat(b.Sources[0], 5).ToArray() }; break;
        }
        var result = Assert.Single(Assess(s, b, known).Orders);
        Assert.False(result.IsSufficient); Assert.Contains(DeribitTerminalEvidenceProblem.BindingInvalid, result.Problems);
    }

    [Theory]
    [InlineData("incident")] [InlineData("count")] [InlineData("disposition")] [InlineData("embedded-scope")]
    [InlineData("received-before-event")] [InlineData("future-receipt")]
    public void InvalidRetainedSnapshotNeverProvidesTerminalAuthority(string mode)
    {
        var s = Snapshot(Report()); var known = DeribitKnownOrderSelection.FromSnapshot(s, Account, Epoch, "ETH-PERPETUAL"); var row = s.Observations[0];
        s = mode switch {
            "incident" => s with { Problems = new[] { OmsPrivateRecoveryReadProblem.IntegrityIncident } },
            "count" => s with { AcceptedFactCount = 2 }, "disposition" => s with { Observations = new[] { row with { Disposition = OmsPrivateCaptureResult.Collision } } },
            "embedded-scope" => s with { Observations = new[] { row with { Envelope = row.Envelope with {
                Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) } } } },
            "received-before-event" => s with { Observations = new[] { row with { Envelope = row.Envelope with {
                Observation = row.Envelope.Observation with { ReceivedAtUtc = From.AddMinutes(-1) } } } } },
            _ => s with { Observations = new[] { row with { Envelope = row.Envelope with {
                Observation = row.Envelope.Observation with { ReceivedAtUtc = Now.AddSeconds(1) } } } } } };
        var result = Assert.Single(Assess(s, known: known).Orders);
        Assert.False(result.IsSufficient); Assert.Contains(DeribitTerminalEvidenceProblem.SnapshotInvalid, result.Problems);
    }

    [Theory]
    [InlineData("open", DeribitTerminalEvidenceProblem.MissingTerminalReport)]
    [InlineData("no-cumulative", DeribitTerminalEvidenceProblem.MissingCumulative)]
    [InlineData("bad-filled", DeribitTerminalEvidenceProblem.InvalidTerminalQuantity)]
    [InlineData("bad-rejected", DeribitTerminalEvidenceProblem.InvalidTerminalQuantity)]
    [InlineData("uncovered-report", DeribitTerminalEvidenceProblem.MissingTerminalReport)]
    [InlineData("terms", DeribitTerminalEvidenceProblem.TermsMismatch)]
    [InlineData("same-time", DeribitTerminalEvidenceProblem.ConflictingReports)]
    [InlineData("reopened", DeribitTerminalEvidenceProblem.ConflictingReports)]
    [InlineData("regression", DeribitTerminalEvidenceProblem.ConflictingReports)]
    [InlineData("terminal-change", DeribitTerminalEvidenceProblem.ConflictingReports)]
    [InlineData("overfill", DeribitTerminalEvidenceProblem.LedgerMismatch)]
    [InlineData("late-venue-fill", DeribitTerminalEvidenceProblem.ExecutionAfterTerminal)]
    [InlineData("missing-fee", DeribitTerminalEvidenceProblem.MissingExecutionDetails)]
    [InlineData("overflow", DeribitTerminalEvidenceProblem.QuantityOverflow)]
    public void InconsistentOrIncompleteOrderEvidenceFailsClosed(string mode, DeribitTerminalEvidenceProblem problem)
    {
        var report = Report(filled: 4); var fill = Fill(); var events = new List<OmsPrivateEventEnvelope> { Prior(), report, fill }; var b = Batch();
        switch (mode)
        {
            case "open": events = new() { Report("open") }; break;
            case "no-cumulative": events[1] = report with { Facts = report.Facts with { FilledNativeAmount = null } }; break;
            case "bad-filled": events[1] = report with { Facts = report.Facts with { OrderState = "filled" } }; break;
            case "bad-rejected": events[1] = report with { Facts = report.Facts with { OrderState = "rejected" } }; break;
            case "uncovered-report": b = b with { ThroughUtc = report.Facts.VenueTimestampUtc.AddSeconds(-1) }; break;
            case "terms": events[2] = fill with { Facts = fill.Facts with { Side = OrderSide.Sell } }; break;
            case "same-time": events.Add(report with { Facts = report.Facts with { Price = 2001 } }); break;
            case "reopened": events.Add(Report("open", filled: 4, minute: 3)); break;
            case "regression": events[0] = Prior(5); break;
            case "terminal-change": events.Add(Report("filled", filled: 10, minute: 3)); break;
            case "overfill": events.Add(Fill(1, "extra")); break;
            case "late-venue-fill": events[2] = fill with { Facts = fill.Facts with { VenueTimestampUtc = From.AddMinutes(3) } }; break;
            case "missing-fee": events[2] = fill with { Facts = fill.Facts with { Fee = null, FeeCurrency = null } }; break;
            case "overflow": events = new() { Report("filled", decimal.MaxValue, decimal.MaxValue), Fill(decimal.MaxValue), Fill(1, "extra") }; break;
        }
        var s = Snapshot(events.ToArray());
        // Deliberately bypass seed selection only to exercise raw contradictory snapshot validation in this component.
        var seed = events.Last(x => x.Facts.Kind == OmsJournalEventKind.Order).Facts;
        var known = new DeribitKnownOrderReadRequest(Account, Epoch, s.SnapshotId, new[] { seed });
        var result = Assert.Single(Assess(s, b, known).Orders);
        Assert.False(result.IsSufficient); Assert.Contains(problem, result.Problems);
    }

    [Theory]
    [InlineData("price")] [InlineData("fee")] [InlineData("quantity")]
    public void ImmutableExecutionCollisionAcrossRetainedAndCurrentCannotBeIgnored(string field)
    {
        var fill = Fill(); var changed = field switch {
            "price" => fill with { Facts = fill.Facts with { Price = 2001 } },
            "fee" => fill with { Facts = fill.Facts with { Fee = 0.01m } },
            _ => fill with { Facts = fill.Facts with { NativeAmount = 3 } } };
        var result = Assert.Single(Assess(Snapshot(Report(filled: 4), fill), Batch(Current(changed))).Orders);
        Assert.False(result.IsSufficient); Assert.Contains(DeribitTerminalEvidenceProblem.ExecutionCollision, result.Problems);
    }
}
