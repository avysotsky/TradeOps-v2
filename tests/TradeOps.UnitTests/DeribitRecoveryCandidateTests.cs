using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryCandidateTests
{
    [Fact]
    public void PartialFillAndDuplicateExecutionsHaveOneDurableNativeLeg()
    {
        var fill = Fill(); var document = Document([Prior()], [Report("cancelled", 4), fill, fill]);
        var result = DeribitRecoveryCandidateBuilder.Build(document)!;
        var order = Assert.Single(result.Orders.Orders); Assert.Equal("cancelled", order.NativeState);
        Assert.Equal(4, order.KnownFilledNativeAmount); Assert.Single(result.Executions);
        Assert.Equal(1, result.Orders.DistinctExecutions);
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.IncompleteCoverage);
        Assert.False(result.Published); Assert.False(result.AccountReady);
    }

    [Fact]
    public void EmptyOpenSnapshotCannotFabricateCancellation()
    {
        var result = DeribitRecoveryCandidateBuilder.Build(Document([Prior()], []))!;
        Assert.Null(Assert.Single(result.Orders.Orders).NativeState);
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.MissingOrderEvidence);
    }

    [Fact]
    public void ConflictingFillHasNoArbitraryWinningPriceOrFee()
    {
        var fill = Fill(); var changed = fill with { Facts = fill.Facts with { Price = 2001 } };
        var result = DeribitRecoveryCandidateBuilder.Build(Document([Prior()], [Report("cancelled", 4), fill, changed]))!;
        Assert.Empty(result.Executions);
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.ExecutionCollision);
    }

    [Fact]
    public void UnknownExternalOrderAndCancelFillRaceRemainUnresolved()
    {
        var result = DeribitRecoveryCandidateBuilder.Build(Document([], [Report("cancelled", 0), Fill()]))!;
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.ExternalOrder);
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.FillMismatch);
        Assert.Single(result.Executions); Assert.False(result.Orders.IsConsistent);
    }

    [Fact]
    public void ForeignScopeOrEpochInVenueFactsCannotEnterCandidate()
    {
        var document = Document([Prior()], [Report()]); var query = document.QueryObservations!;
        var original = query.Observations[0];
        foreach (var changed in new[] {
            original with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) },
            original with { Observation = original.Observation with { StreamEpoch = Guid.NewGuid() } } })
            Assert.Null(DeribitRecoveryCandidateBuilder.Build(document with { QueryObservations = query with { Observations = [changed] } }));
    }

    private static DeribitRecoveryRunDocumentV1 Document(OmsPrivateEventEnvelope[] prior, OmsPrivateEventEnvelope[] current)
    {
        var start = new OmsRecoveryRunStart(Account, Guid.NewGuid(), Epoch, "ETH-PERPETUAL", "ETH", From, Through);
        var snapshot = new OmsPrivateRecoveryReadResult(Account, Guid.NewGuid(), Through, prior.Length,
            prior.Select(x => new OmsPrivateRecoveryObservation(x, OmsPrivateCaptureResult.Inserted)).ToArray(), []);
        var gate = new DeribitRecoveryGateAssessment(Account, Epoch, snapshot.SnapshotId, "ETH-PERPETUAL", From, Through,
            Through.AddSeconds(1), prior.Length, current.Length, 0, 0, 0, [DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable]);
        var assessment = new DeribitRecoveryAccountLedgerAssessment(new(gate, null, false, 1, 0, false,
            [DeribitRecoveryAccountProblem.ReadSkipped]), new(gate, 0, 0, []));
        var query = new DeribitRecoveryQueryResult(Account, Epoch, "ETH-PERPETUAL", "ETH", From.AddSeconds(-1), Through,
            From, Through, new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), current, []);
        return new(1, start, assessment, new(snapshot, [], []), null, query);
    }
}
