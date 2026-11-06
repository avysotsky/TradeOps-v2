using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitOrderRecoveryProjectorTests
{
    internal static readonly AccountKey Account = new(new("tenant"), new("Deribit"), OmsEnvironment.Testnet, new("account"));
    internal static readonly Guid Epoch = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly DateTimeOffset From = new(2026, 11, 4, 12, 10, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Through = From.AddMinutes(10);
    internal static readonly DeribitOrderRecoveryCoverage Complete = new(From.AddMinutes(-1), Through,
        true, true, true, true);

    internal static OmsPrivateEventEnvelope Report(string state = "cancelled", decimal filled = 0,
        decimal amount = 10, int minute = 2) => new(Account,
        new(OmsJournalEventKind.Order, "ETH", "ETH-PERPETUAL", "order-1", OrderSide.Buy,
            amount, OmsPrivateAmountUnit.Usd, "USD", From.AddMinutes(minute),
            OrderState: state, FilledNativeAmount: filled, PriceKind: OmsPrivatePriceKind.Numeric, Price: 2000),
        new(Guid.NewGuid(), Epoch, From.AddMinutes(5), OmsPrivateCaptureSource.RecoveryQuery, "recovery/orders"));

    internal static OmsPrivateEventEnvelope Prior(decimal filled = 0) => Report("open", filled, minute: -2) with
    { Observation = new(Guid.NewGuid(), Guid.NewGuid(), From.AddMinutes(-1), OmsPrivateCaptureSource.WebSocket, "orders") };

    internal static OmsPrivateEventEnvelope Fill(decimal amount = 4, string id = "trade-1") => new(Account,
        new(OmsJournalEventKind.Execution, "ETH", "ETH-PERPETUAL", "order-1", OrderSide.Buy,
            amount, OmsPrivateAmountUnit.Usd, "USD", From.AddMinutes(1), ExchangeTradeId: id,
            PriceKind: OmsPrivatePriceKind.Numeric, Price: 2000, Fee: -0.0001m, FeeCurrency: "ETH"),
        new(Guid.NewGuid(), Epoch, From.AddMinutes(5), OmsPrivateCaptureSource.RecoveryQuery, "recovery/trades"));

    internal static DeribitOrderRecoveryResult Project(OmsPrivateEventEnvelope[] recovered,
        OmsPrivateEventEnvelope[]? prior = null, DeribitOrderRecoveryCoverage? coverage = null) =>
        DeribitOrderRecoveryProjector.Rebuild(Account, Epoch, From, Through, coverage ?? Complete,
            prior ?? [Prior()], recovered);

    private static void Has(DeribitOrderRecoveryResult result, DeribitOrderRecoveryProblem problem)
    {
        Assert.False(result.IsConsistent);
        Assert.Contains(result.Issues, x => x.Problem == problem);
    }

    [Fact]
    public void ExplicitCancel_RebuildsWithoutInventingExecution()
    {
        var result = Project([Report()]);
        Assert.True(result.IsConsistent);
        Assert.Equal(Account, result.Scope);
        Assert.Equal(Epoch, result.Epoch);
        Assert.Equal(From, result.RequiredFromUtc);
        Assert.Equal(Through, result.ThroughUtc);
        var order = Assert.Single(result.Orders);
        Assert.Equal("cancelled", order.NativeState);
        Assert.Equal(0, order.KnownFilledNativeAmount);
        Assert.Equal(0, result.DistinctExecutions);
    }

    [Theory]
    [InlineData("filled", "cancelled")]
    [InlineData("cancelled", "filled")]
    public void DifferentTerminalStatesRemainAConflictEvenWithMatchingCumulative(string first, string later)
    {
        var result = Project([Report(first, 10, minute: 2), Report(later, 10, minute: 3), Fill(10)]);
        Has(result, DeribitOrderRecoveryProblem.ConflictingOrderReports);
    }

    [Fact]
    public void EmptyOpenOrders_DoNotImplyCancellation()
    {
        var result = Project([]);
        Has(result, DeribitOrderRecoveryProblem.MissingOrderEvidence);
        Assert.Null(Assert.Single(result.Orders).NativeState);
        Assert.False(result.Orders[0].HasCurrentEvidence);
    }

    [Fact]
    public void CancelAfterPartialFill_PreservesFillAndNativeUsdUnits()
    {
        var result = Project([Report(filled: 4), Fill()]);
        Assert.True(result.IsConsistent);
        Assert.Equal("cancelled", result.Orders[0].NativeState);
        Assert.Equal(4, result.Orders[0].KnownFilledNativeAmount);
        Assert.Equal(10, result.Orders[0].NativeAmount);
        Assert.Equal(OmsPrivateAmountUnit.Usd, result.Orders[0].AmountUnit);
        Assert.Equal("USD", result.Orders[0].AmountCurrency);
    }

    [Fact]
    public void PersistedFillAndOverlappingWsHistoryReceipts_AreCountedOnce()
    {
        var old = Fill() with { Facts = Fill().Facts with { VenueTimestampUtc = From.AddMinutes(-3) } };
        var stored = old with { Observation = old.Observation with
            { StreamEpoch = Guid.NewGuid(), ReceivedAtUtc = From.AddMinutes(-2) } };
        var ws = old with { Observation = old.Observation with
            { CaptureId = Guid.NewGuid(), Source = OmsPrivateCaptureSource.WebSocket, Channel = "trades" } };
        var result = Project([old, ws, Report(filled: 4)], [Prior(4), stored]);
        Assert.True(result.IsConsistent);
        Assert.Equal(1, result.DistinctExecutions);
        Assert.Equal(4, result.Orders[0].KnownFilledNativeAmount);
    }

    [Fact]
    public void MultipleFills_ResolveFilledOnlyWhenExactTotalMatches()
    {
        var result = Project([Fill(), Fill(6, "trade-2"), Report("filled", 10)]);
        Assert.True(result.IsConsistent);
        Assert.Equal(2, result.DistinctExecutions);
        Assert.Equal(10, result.Orders[0].KnownFilledNativeAmount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void IncompleteOrGappedQueryCoverage_NeverClearsComparison(int change)
    {
        var coverage = change switch
        {
            0 => Complete with { OpenOrdersComplete = false },
            1 => Complete with { OrderHistoryComplete = false },
            2 => Complete with { TradeHistoryComplete = false },
            3 => Complete with { BufferedReplayComplete = false },
            4 => Complete with { GapDetected = true },
            5 => Complete with { FromUtc = From.AddSeconds(1) },
            _ => Complete with { ThroughUtc = Through.AddSeconds(-1) }
        };
        Has(Project([Report()], coverage: coverage), DeribitOrderRecoveryProblem.IncompleteCoverage);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ForeignScope_IsRejectedEvenWithIdenticalNativeOrderId(int change)
    {
        var scope = change switch
        {
            0 => new AccountKey(new("other"), Account.Venue, Account.Environment, Account.Account),
            1 => new AccountKey(Account.Tenant, Account.Venue, OmsEnvironment.Mainnet, Account.Account),
            2 => new AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("other")),
            _ => new AccountKey(Account.Tenant, Account.Venue, Account.Environment, Account.Account, new TradingSubaccountId("other"))
        };
        var result = Project([Report() with { Scope = scope }]);
        Has(result, DeribitOrderRecoveryProblem.ScopeMismatch);
        Assert.Null(result.Orders[0].NativeState);
    }

    [Fact]
    public void StaleEpoch_CannotSupplyCurrentOrderEvidence()
    {
        var report = Report();
        Has(Project([report with { Observation = report.Observation with { StreamEpoch = Guid.NewGuid() } }]),
            DeribitOrderRecoveryProblem.EpochMismatch);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FactsOutsideClosedWindow_AreNotAdopted(int change)
    {
        var report = Report();
        report = change switch
        {
            0 => report with { Facts = report.Facts with { VenueTimestampUtc = Through.AddSeconds(1) } },
            1 => report with { Observation = report.Observation with { ReceivedAtUtc = Through.AddSeconds(1) } },
            _ => report with { Observation = report.Observation with { ReceivedAtUtc = From.AddSeconds(-1) } }
        };
        Has(Project([report]), DeribitOrderRecoveryProblem.OutsideWindow);
    }

    [Fact]
    public void ExchangeOnlyOrder_IsVisibleAndRequiresReview()
    {
        var report = Report();
        var result = Project([report with { Facts = report.Facts with { ExchangeOrderId = "external" } }]);
        Has(result, DeribitOrderRecoveryProblem.ExternalOrder);
        Assert.Contains(result.Orders, x => x.ExchangeOrderId == "external" && !x.PreviouslyObserved);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ConflictingExecutionIdentity_IsNotArbitrarilyChosen(int change)
    {
        var fill = Fill();
        var facts = change switch
        {
            0 => fill.Facts with { NativeAmount = 5 },
            1 => fill.Facts with { Price = 2001 },
            2 => fill.Facts with { Fee = 0.001m },
            _ => fill.Facts with { NativeInstrument = "ETH-OTHER" }
        };
        var result = Project([Report(filled: 4), fill, fill with { Facts = facts }]);
        Has(result, DeribitOrderRecoveryProblem.ExecutionCollision);
        Assert.Equal(0, result.DistinctExecutions);
    }

    [Fact]
    public void LateFillAfterCancel_RequiresFreshCumulativeEvidence()
    {
        Has(Project([Report(), Fill(1)]), DeribitOrderRecoveryProblem.FillMismatch);
    }

    [Fact]
    public void MissingRetainedFills_CannotBeReplacedWithCumulativeQuantity()
    {
        var result = Project([Report(filled: 4)], [Prior(4)]);
        Has(result, DeribitOrderRecoveryProblem.FillMismatch);
        Assert.Equal(0, result.DistinctExecutions);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void TermsOrAmountChange_RequiresExplicitRevisionRecovery(int change)
    {
        var report = Report();
        var facts = change switch
        {
            0 => report.Facts with { NativeInstrument = "ETH-OTHER" },
            1 => report.Facts with { Side = OrderSide.Sell },
            _ => report.Facts with { NativeAmount = 11 }
        };
        Has(Project([report with { Facts = facts }]), DeribitOrderRecoveryProblem.TermsChanged);
    }

    [Fact]
    public void LaterCumulativeRegression_IsBlocked()
    {
        Has(Project([Report(filled: 0)], [Prior(4)]), DeribitOrderRecoveryProblem.CumulativeRegression);
    }

    [Fact]
    public void TerminalOrderCannotReopenWithLaterTimestamp()
    {
        Has(Project([Report(), Report("open", minute: 3)]), DeribitOrderRecoveryProblem.TerminalReopened);
    }

    [Fact]
    public void SameVenueTimestampConflictingReports_AreBlocked()
    {
        Has(Project([Report(), Report("open")]), DeribitOrderRecoveryProblem.ConflictingOrderReports);
    }

    [Fact]
    public void MissingCumulative_IsUnknownRatherThanZero()
    {
        var report = Report();
        Has(Project([report with { Facts = report.Facts with { FilledNativeAmount = null } }]),
            DeribitOrderRecoveryProblem.MissingCumulative);
    }

    [Theory]
    [InlineData("filled", 4)] [InlineData("rejected", 4)]
    public void TerminalQuantityMustMatchNativeMeaning(string state, decimal filled)
    {
        Has(Project([Report(state, filled), Fill(filled)]), DeribitOrderRecoveryProblem.InvalidOrderQuantity);
    }

    [Fact]
    public void UnknownOldCumulative_IsResolvedByExplicitCurrentEvidenceAndLedger()
    {
        var prior = Prior();
        var result = Project([Report(filled: 4), Fill()],
            [prior with { Facts = prior.Facts with { FilledNativeAmount = null } }]);
        Assert.True(result.IsConsistent);
        Assert.Equal(4, result.Orders[0].KnownFilledNativeAmount);
    }

    [Fact]
    public void OpenOrderWithFullExecution_IsNotCoherent()
    {
        Has(Project([Report("open", 10), Fill(10)]), DeribitOrderRecoveryProblem.InvalidOrderQuantity);
    }

    [Fact]
    public void ExplicitOpenLeavesMustReconcileWithTotalAndFills()
    {
        var report = Report("open", 4);
        Has(Project([report with { Facts = report.Facts with { LeavesNativeAmount = 3 } }, Fill()]),
            DeribitOrderRecoveryProblem.InvalidOrderQuantity);
    }

    [Fact]
    public void ArrivalOrderDoesNotChangeProjectionOrIssues()
    {
        var input = new[] { Report("open", 4, minute: 1), Report(filled: 4), Fill(), Fill() };
        var expected = Project(input);
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var actual = Project(input.OrderBy(_ => random.Next()).ToArray());
            Assert.Equal(expected.Orders, actual.Orders);
            Assert.Equal(expected.Issues, actual.Issues);
            Assert.Equal(expected.DistinctExecutions, actual.DistinctExecutions);
        }
        Assert.True(expected.IsConsistent);
    }

    [Fact]
    public void QuantityOverflow_BlocksInsteadOfWrappingOrClamping()
    {
        var result = Project([Report("filled", decimal.MaxValue, decimal.MaxValue),
            Fill(decimal.MaxValue), Fill(decimal.MaxValue, "trade-2")], [Prior()]);
        Has(result, DeribitOrderRecoveryProblem.QuantityOverflow);
        Assert.Null(result.Orders[0].KnownFilledNativeAmount);
    }

    [Fact]
    public void SameTradeIdOnAnotherOwnOrderLeg_RemainsSeparate()
    {
        var otherPrior = Prior();
        var otherReport = Report(filled: 4);
        var otherFill = Fill();
        var result = Project([Report(filled: 4), Fill(),
            otherReport with { Facts = otherReport.Facts with { ExchangeOrderId = "order-2" } },
            otherFill with { Facts = otherFill.Facts with { ExchangeOrderId = "order-2" } }],
            [Prior(), otherPrior with { Facts = otherPrior.Facts with { ExchangeOrderId = "order-2" } }]);
        Assert.True(result.IsConsistent);
        Assert.Equal(2, result.DistinctExecutions);
        Assert.Equal(2, result.Orders.Count);
        Assert.All(result.Orders, x => Assert.Equal(4, x.KnownFilledNativeAmount));
    }

    [Fact]
    public void ForeignPersistedLedger_CannotBeAdoptedAsLocalBaseline()
    {
        var prior = Prior();
        var foreign = new AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("foreign"));
        var result = Project([Report()], [prior with { Scope = foreign }]);
        Has(result, DeribitOrderRecoveryProblem.ScopeMismatch);
        Has(result, DeribitOrderRecoveryProblem.ExternalOrder);
        Assert.False(result.Orders[0].PreviouslyObserved);
    }

    [Fact]
    public void OversizedBatch_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Project(Enumerable.Repeat(Report(),
            DeribitOrderRecoveryProjector.MaximumFactsPerSet + 1).ToArray()));
    }

    [Fact]
    public void ReversedWindow_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => DeribitOrderRecoveryProjector.Rebuild(Account, Epoch,
            Through, From, Complete, [], []));
    }
}
