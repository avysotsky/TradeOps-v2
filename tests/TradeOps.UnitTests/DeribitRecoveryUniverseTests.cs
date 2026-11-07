using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryUniverseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static OmsRecoveryLedgerSnapshot Ledger() => new(new(Account, Guid.NewGuid(), Now, 0, [], []), [], []);
    private static DeribitAccountInventoryResult Inventory() => new(Account, Epoch, Now, Now, TimeSpan.FromSeconds(30),
        new[] { "ETH", "BTC", "USDC" }.Select(x => new DeribitCurrencyBalance(x, 0, 0, false, false, new(Now, Now))).ToArray(), [], [], []);
    private static DeribitRecoveryInstrument Metadata(string name) => new(name, "ETH", "future", OmsPrivateAmountUnit.Usd, "USD");

    [Fact]
    public void ZeroCurrentHoldingsStillRequireEveryObservedCurrencyHistory()
    {
        var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), Inventory(), [], Now);
        Assert.True(result.UniverseResolved); Assert.Equal(new[] { "BTC", "ETH", "USDC" }, result.RequiredCurrencies);
        Assert.Empty(result.RequiredInstruments); Assert.False(result.AccountReady);
    }

    [Fact]
    public void CurrentActivityAndJournalCommandsAndRetainedExecutionsAllEnterUniverse()
    {
        var command = OmsRecoveryLedgerPostgresTests.Command(); command = command with { Payload = command.Payload with { Instrument = "ETH-COMMAND" } };
        var ledger = Ledger(); ledger = ledger with { Commands = [new(command, 1, OmsRecoveryDispatchPhase.PersistedNotSent, OmsRecoveryOutboxState.NotSent, 0, null)],
            Private = ledger.Private with { AcceptedFactCount = 1, Observations = [new(Fill(), OmsPrivateCaptureResult.Inserted)] } };
        var inventory = Inventory() with { Positions = [new("ETH-POSITION", "future", "buy", 1)], OpenOrders = [new("ETH-OPEN", "id", "open", "limit")] };
        var names = new[] { "ETH-PERPETUAL", "ETH-COMMAND", "ETH-POSITION", "ETH-OPEN" };
        var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, ledger, inventory, names.Select(Metadata).ToArray(), Now);
        Assert.True(result.UniverseResolved); Assert.Equal(names.Order(StringComparer.Ordinal), result.RequiredInstruments);
    }

    [Fact]
    public void UnknownInstrumentStaysVisibleAndCannotAcquireInferredCurrencyOrUnits()
    {
        var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), Inventory() with {
            Positions = [new("UNKNOWN", "future_combo", "sell", -1)] }, [], Now);
        Assert.False(result.UniverseResolved); Assert.Contains("UNKNOWN", result.RequiredInstruments);
        Assert.Contains(result.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.UnmappedInstrument && x.NativeInstrument == "UNKNOWN");
    }

    [Fact]
    public void ForeignInventoryAndEpochDoNotContributeAnyReferences()
    {
        var original = Inventory() with { Positions = [new("foreign-symbol", "future", "buy", 1)] };
        foreach (var changed in new[] { original with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) }, original with { Epoch = Guid.NewGuid() } })
        {
            var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), changed, [], Now);
            Assert.Contains(result.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.InvalidInventory);
            Assert.Empty(result.RequiredInstruments); Assert.Empty(result.RequiredCurrencies);
        }
    }

    [Fact]
    public void InventoryFreshnessIsCheckedAtConsumption()
    {
        var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), Inventory(), [], Now.AddSeconds(31));
        Assert.Contains(result.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.StaleInventory); Assert.Empty(result.RequiredInstruments);
    }

    [Fact]
    public void DuplicateMetadataAndForeignJournalAreNotCoverageProof()
    {
        var duplicate = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), Inventory(), [Metadata("X"), Metadata("X")], Now);
        Assert.Contains(duplicate.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.InvalidManifest);
        var ledger = Ledger(); ledger = ledger with { Private = ledger.Private with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) } };
        Assert.Contains(DeribitRecoveryUniverseBuilder.Build(Account, Epoch, ledger, Inventory(), [], Now).Issues,
            x => x.Problem == DeribitRecoveryUniverseProblem.InvalidJournal);
    }

    [Fact]
    public void UnrecognizedCurrencyIsAnExplicitIssueEvenWithZeroBalance()
    {
        var inventory = Inventory() with { Balances = [new("UNSUPPORTED", 0, 0, false, false, new(Now, Now))] };
        var result = DeribitRecoveryUniverseBuilder.Build(Account, Epoch, Ledger(), inventory, [], Now);
        Assert.Contains(result.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.UnsupportedCurrency && x.Currency == "UNSUPPORTED");
    }

    [Fact]
    public void ExplicitV2KeepsCollateralInventorySeparateFromEveryDerivativeSettlementHistory()
    {
        var inventory=Inventory() with{Balances=[..Inventory().Balances,new("BUIDL",0,0,true,true,new(Now,Now)),new("SOL",1,1,true,true,new(Now,Now))]};
        var result=DeribitRecoveryUniverseBuilder.Build(Account,Epoch,Ledger(),inventory,[],Now,DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2);
        Assert.True(result.UniverseResolved);Assert.Equal(new[]{"BTC","ETH","EURR","USDC","USDT"},result.RequiredCurrencies);
        var coverage=result.HistoryCoverage!;Assert.Equal(2,coverage.SchemaVersion);
        Assert.Equal(new[]{"BTC","BUIDL","ETH","SOL","USDC"},coverage.CollateralCurrencies);
        Assert.Equal(new[]{"BUIDL","SOL"},coverage.UnqueriedCollateralCashCurrencies);
        Assert.Equal(5,inventory.Balances.Count);Assert.False(result.AccountReady);
        var v1=DeribitRecoveryUniverseBuilder.Build(Account,Epoch,Ledger(),inventory,[],Now);
        Assert.False(v1.UniverseResolved);Assert.Null(v1.HistoryCoverage);
        Assert.DoesNotContain("historyCoverage",OmsRecoveryRunCodec.Encode(v1).Json);
    }
    [Theory]
    [InlineData("unknown-collateral")][InlineData("unknown-instrument")][InlineData("unsupported-settlement")]
    public void V2DoesNotHideUnrecognizedCurrenciesOrInferHistoryForUnsupportedInstruments(string problem)
    {
        var inventory=Inventory();DeribitRecoveryInstrument[] manifest=[];
        if(problem=="unknown-collateral")inventory=inventory with{Balances=[new("UNKNOWN",0,0,false,false,new(Now,Now))]};
        else
        {
            inventory=inventory with{Positions=[new("SOL-TEST","future","buy",1)]};
            if(problem=="unsupported-settlement")manifest=[new("SOL-TEST","SOL","future",OmsPrivateAmountUnit.BaseCoin,"SOL")];
        }
        var result=DeribitRecoveryUniverseBuilder.Build(Account,Epoch,Ledger(),inventory,manifest,Now,DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2);
        Assert.False(result.UniverseResolved);
        Assert.Contains(result.Issues,x=>x.Problem==(problem=="unknown-instrument"?DeribitRecoveryUniverseProblem.UnmappedInstrument:DeribitRecoveryUniverseProblem.UnsupportedCurrency));
    }
}
