using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitTerminalEvidenceGateTests
{
    internal sealed class Ports : IOmsPrivateRecoveryReader, IDeribitRecoveryQueries, IDeribitKnownOrderReader
    {
        internal OmsPrivateRecoveryReadResult Snapshot = DeribitTerminalEvidenceTests.Snapshot(Report());
        internal DeribitRecoveryQueryResult Batch = DeribitTerminalEvidenceTests.Batch();
        internal int Lookups; internal bool LiveClock; internal static readonly DateTimeOffset Now = DeribitTerminalEvidenceTests.Now;
        internal DeribitRecoveryEvidenceGate Gate(IOmsPrivateRecoveryReader? reader = null) => new(reader ?? this, this, this, new Clock(this));
        internal DeribitRecoveryGateRequest Request() => new(new(Account, Epoch, "ETH-PERPETUAL", From,
            (LiveClock ? DateTimeOffset.UtcNow : Now).AddSeconds(-5), TimeSpan.FromHours(1)), "ETH", OmsPrivateAmountUnit.Usd, "USD");
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery query, CancellationToken ct = default) => Task.FromResult(
            Batch with { StartedAtUtc = LiveClock ? DateTimeOffset.UtcNow : Now, CompletedAtUtc = LiveClock ? DateTimeOffset.UtcNow : Now,
                QueryFromUtc = DateTimeOffset.FromUnixTimeMilliseconds((query.FromUtc-query.Overlap).ToUnixTimeMilliseconds()),
                ThroughUtc = DateTimeOffset.FromUnixTimeMilliseconds(query.ThroughUtc.ToUnixTimeMilliseconds()) });
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
        { Lookups++; throw new InvalidOperationException("Expired terminal lookup must not be retried."); }
        private sealed class Clock(Ports owner) : TimeProvider { public override DateTimeOffset GetUtcNow() => owner.LiveClock ? DateTimeOffset.UtcNow : Now; }
    }

    [Theory]
    [InlineData("cancelled", 0)] [InlineData("cancelled", 4)] [InlineData("filled", 10)]
    public async Task ProvenTerminalSkipsLookupButLeavesAllGlobalCoverageGates(string state, decimal filled)
    {
        var p = new Ports(); p.Snapshot = filled == 0 ? DeribitTerminalEvidenceTests.Snapshot(Report(state))
            : DeribitTerminalEvidenceTests.Snapshot(Report(state, filled), Fill(filled));
        var result = await p.Gate().AssessAsync(p.Request());
        Assert.Equal(1, result.RetentionExpiredOrders); Assert.Equal(1, result.RetainedTerminalOrdersValidated);
        Assert.Equal(0, p.Lookups); Assert.Equal(0, result.AddressedOrders);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.KnownOrderRetentionExpired, result.Blockers);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable, result.Blockers);
        GlobalGates(result);
    }

    [Theory]
    [InlineData("missing-fill")] [InlineData("new-fill")] [InlineData("missing-cumulative")]
    public async Task InsufficientTerminalEvidenceBlocksWithoutUnavailableLookup(string mode)
    {
        var p = new Ports();
        if (mode == "missing-fill") p.Snapshot = DeribitTerminalEvidenceTests.Snapshot(Report(filled: 4));
        if (mode == "missing-cumulative") p.Snapshot = DeribitTerminalEvidenceTests.Snapshot(Report() with { Facts = Report().Facts with { FilledNativeAmount = null } });
        if (mode == "new-fill") p.Batch = p.Batch with { Observations = new[] { Fill() with { Observation =
            new(Guid.NewGuid(), Epoch, Ports.Now, OmsPrivateCaptureSource.RecoveryQuery, "private/get_user_trades_by_instrument") } } };
        var result = await p.Gate().AssessAsync(p.Request());
        Assert.Equal(0, result.RetainedTerminalOrdersValidated); Assert.Equal(0, p.Lookups);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.RetainedTerminalEvidenceInsufficient, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.KnownOrderRetentionExpired, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable, result.Blockers); GlobalGates(result);
    }

    [Fact]
    public async Task OneProvenTerminalCannotHideAnotherUnreconciledTerminal()
    {
        var good = Report(); var bad = Report(filled: 4) with { Facts = Report(filled: 4).Facts with { ExchangeOrderId = "order-2" } };
        var p = new Ports { Snapshot = DeribitTerminalEvidenceTests.Snapshot(good, bad) };
        var result = await p.Gate().AssessAsync(p.Request());
        Assert.Equal(2, result.KnownOrders); Assert.Equal(2, result.RetentionExpiredOrders); Assert.Equal(1, result.RetainedTerminalOrdersValidated);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable, result.Blockers); Assert.Equal(0, p.Lookups); GlobalGates(result);
    }

    internal static void GlobalGates(DeribitRecoveryGateAssessment result)
    {
        foreach (var b in new[] { DeribitRecoveryEvidenceBlocker.HistoricalUnfilledCoverageGap, DeribitRecoveryEvidenceBlocker.OrderHistoryIncomplete,
            DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven })
            Assert.Contains(b, result.Blockers);
        Assert.False(result.CanRebuildOrders); Assert.False(result.AccountReady); Assert.False(result.DurableProjectionPublished);
    }
}
