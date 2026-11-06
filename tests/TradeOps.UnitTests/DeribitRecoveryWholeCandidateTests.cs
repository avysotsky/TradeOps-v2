using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryWholeCandidateTests
{
    [Fact]
    public void AllCurrencyEvidencePreservesNativeIdentityNamespacesAndExplicitPositionUnits()
    {
        var result = DeribitRecoveryCandidateBuilder.Build(Document())!;
        Assert.Equal(DeribitRecoveryCandidateScope.InventoryCurrencies, result.CoverageScope);
        Assert.Equal(2, result.Orders.Orders.Count); Assert.Equal(2, result.Executions.Count); Assert.Equal(2, result.Orders.DistinctExecutions);
        Assert.All(result.Orders.Orders, x => Assert.Equal("shared-order", x.ExchangeOrderId));
        Assert.Equal("USD", result.Positions.Single(x => x.Kind == "future").SizeCurrency);
        Assert.Equal("BTC", result.Positions.Single(x => x.Kind == "option").SizeCurrency);
        Assert.Equal(new[] { "BTC", "ETH" }, result.CurrencyBalances!.Select(x => x.Currency));
        Assert.Null(result.Balance); Assert.False(result.Published); Assert.False(result.AccountReady);
        Assert.Contains(result.Orders.Issues, x => x.Problem == DeribitOrderRecoveryProblem.IncompleteCoverage);
    }

    [Fact]
    public void MissingCurrencyBatchRemainsPartialAndRecordsItsGap()
    {
        var document = Document(); document = document with { CurrencyObservations = document.CurrencyObservations!.Where(x => x.Currency == "ETH").ToArray() };
        var result = DeribitRecoveryCandidateBuilder.Build(document)!;
        Assert.Equal(DeribitRecoveryCandidateScope.Instrument, result.CoverageScope); Assert.Null(result.CurrencyBalances);
        Assert.Contains(result.UniverseIssues!, x => x.Problem == DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable && x.Currency == "BTC");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingBaseObservationOrUnqualifiedLinearPositionCannotInheritOrderQuantityUnits(bool linear)
    {
        var document = Document(); var inventory = document.InventoryObservations!;
        if (linear)
        {
            inventory = inventory with { Positions = inventory.Positions.Append(new("LINEAR", "future", "buy", 50000, 0.5m)).ToArray() };
            var u = document.Universe!; document = document with { Universe = u with { InstrumentTerms = u.InstrumentTerms!.Append(
                new("LINEAR", "USDC", "future", OmsPrivateAmountUnit.BaseCoin, "BTC", "BTC", "USDC")).ToArray() } };
        }
        else inventory = inventory with { Positions = inventory.Positions.Select(x => x.Kind == "future" ? x with { NativeBaseSize = null } : x).ToArray() };
        var result = DeribitRecoveryCandidateBuilder.Build(document with { InventoryObservations = inventory })!;
        Assert.Empty(result.Positions); Assert.NotNull(result.CurrencyBalances);
        Assert.Contains(result.UniverseIssues!, x => x.Problem == DeribitRecoveryUniverseProblem.PositionUnitsUnavailable);
    }

    [Fact]
    public void MarkChangeCanRefreshBaseObservationWithoutChangingNativePositionSize()
    {
        var document = WithScopedAccount(Document(), 10, 0.004m, 0.4m);
        var result = DeribitRecoveryCandidateBuilder.Build(document)!;
        Assert.Equal(0.004m, result.Positions.Single(x => x.Kind == "future").BaseSize);
        Assert.DoesNotContain(result.UniverseIssues!, x => x.Problem == DeribitRecoveryUniverseProblem.AccountObservationsChanged);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void PositionOrCashChangeBetweenActualObservationsBlocksAccountModel(bool position)
    {
        var document = WithScopedAccount(Document(), position ? 11 : 10, 0.005m, position ? 0.4m : 0.5m);
        var result = DeribitRecoveryCandidateBuilder.Build(document)!;
        Assert.Empty(result.Positions); Assert.Null(result.CurrencyBalances);
        Assert.Contains(result.UniverseIssues!, x => x.Problem == DeribitRecoveryUniverseProblem.AccountObservationsChanged);
    }

    private static DeribitRecoveryRunDocumentV1 WithScopedAccount(DeribitRecoveryRunDocumentV1 d, decimal size, decimal baseSize, decimal cash)
    {
        var at = Through.AddMilliseconds(100);
        var account = new DeribitAccountSnapshotResult(Account, Epoch, "ETH", "future", at, at, TimeSpan.FromSeconds(30), ["ETH-PERPETUAL"],
            new("ETH", cash, 0.6m, false, false, new(at, at)), new(at, at), [new("ETH-PERPETUAL", "future", "buy", size, "USD", baseSize, "ETH")], []);
        return d with { AccountObservations = account, Assessment = d.Assessment with { Account = d.Assessment.Account with { ScopedAccountObservationsValidated = true } } };
    }

    private static DeribitRecoveryRunDocumentV1 Document()
    {
        var document = DeribitRecoveryCandidateTests.Document([], []);
        var at = Through;
        var inventory = new DeribitAccountInventoryResult(Account, Epoch, at, at, TimeSpan.FromSeconds(30),
            [new("BTC", 0.3m, 0.3m, false, false, new(at, at)), new("ETH", 0.4m, 0.4m, false, false, new(at, at))],
            [new("ETH-PERPETUAL", "future", "buy", 10, 0.005m), new("BTC-OPTION", "option", "buy", 2)], [], []);
        var terms = new[] { new DeribitRecoveryInstrument("ETH-PERPETUAL", "ETH", "future", OmsPrivateAmountUnit.Usd, "USD", "ETH", "USD"),
            new("BTC-PERPETUAL", "BTC", "future", OmsPrivateAmountUnit.Usd, "USD", "BTC", "USD"),
            new("BTC-OPTION", "BTC", "option", OmsPrivateAmountUnit.BaseCoin, "BTC", "BTC", "BTC") };
        var universe = new DeribitRecoveryUniverse(Account, Epoch, document.Ledger.Private.SnapshotId,
            terms.Select(x => x.NativeInstrument).ToArray(), ["BTC", "ETH"], [], terms);
        var groups = new List<DeribitCurrencyRecoveryResult>();
        foreach (var currency in new[] { "BTC", "ETH" })
        {
            var fill = Fill(1, "shared-trade"); fill = fill with { Facts = fill.Facts with {
                Currency = currency, NativeInstrument = currency + "-PERPETUAL", ExchangeOrderId = "shared-order", FeeCurrency = currency },
                Observation = fill.Observation with { Channel = "private/get_user_trades_by_currency" } };
            var order = Report("filled", 1, 1); order = order with { Facts = order.Facts with {
                Currency = currency, NativeInstrument = currency + "-PERPETUAL", ExchangeOrderId = "shared-order" },
                Observation = order.Observation with { Channel = "private/get_order_history_by_currency" } };
            groups.Add(new(Account, Epoch, currency, "any", From.AddMinutes(-1), Through, From, Through,
                document.QueryObservations!.Sources, [order, fill], []));
        }
        return document with { InventoryObservations = inventory, Universe = universe, CurrencyObservations = groups };
    }
}
