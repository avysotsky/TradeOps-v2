using System.Text.Json.Nodes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitCrossPortfolioRiskTests
{
    private static readonly DateTimeOffset Now=DeribitRiskInputsTests.Now;
    internal static OmsJournalCommandRequest Command()=>DeribitRiskInputsTests.Command() with
    {Payload=DeribitRiskInputsTests.Command().Payload with{Quantity=1,LimitPrice=1750,PostOnly=true}};
    internal static OmsRecoveredAccountStateV1 Baseline()=>DeribitRiskInputsTests.Baseline() with
    {Positions=[],Balances=[new("ETH",.004m,.004m,true,true,new(Now,Now)),new("BTC",0,0,true,true,new(Now,Now)),new("BUIDL",0,0,true,true,new(Now,Now))]};
    internal static OmsRiskPolicyDocumentV1 Policy()=>DeribitRiskInputsTests.Policy() with
    {SchemaVersion=2,Limits=DeribitRiskInputsTests.Policy().Limits with{SchemaVersion=2,MaxOrderUsd=1,MaxPendingUsd=2,MaxPendingCommands=2},
        CrossPortfolio=new(2,"cross_pm",["BTC","ETH","USDT"],3,.1m,"listed-derivatives-settlement-debits-v2")};
    internal static DeribitCrossPmRiskObservationV2 Observation(OmsRecoveredAccountStateV1 b)
    {
        var capital=new DeribitCrossPmCapitalV2("ETH","cross_pm",true,true,8,8,.2m,.1m,.0001m,Now);
        return new(2,b.Scope,b.Epoch,Now,Now,new(Now.UtcDateTime.Date,TimeSpan.Zero),Now,
            [new(DeribitRiskInputsTests.Observation(b).Instruments[0],1,1999,2001,Now)],b.Balances,capital,capital,
            [new(-1,capital with{InitialMarginUsd=.5m}),new(0,capital),new(1,capital with{InitialMarginUsd=.1m,MaintenanceMarginUsd=.05m})],[],
            [new("BTC",0,0),new("ETH",0,0),new("EURR",0,0),new("USDC",0,0),new("USDT",0,0)],new Dictionary<string,decimal>{{"ETH",2000}},.000001m,1700,2300);
    }

    [Fact]
    public void GlobalUsdCapitalIsUsedOnceAndBothFillDirectionsAreReserved()
    {
        var b=Baseline();var c=Command();var o=Observation(b);
        var risk=DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy(),o,Now)!;Assert.NotNull(risk);
        Assert.Equal(3,risk.Valuation.SchemaVersion);Assert.Equal(4,risk.Valuation.EquityUsd);
        Assert.Equal(.4m,risk.Valuation.MarginUsedUsd);Assert.Equal(16+1,risk.Valuation.PortfolioGrossUsd);
        Assert.True(risk.Valuation.IncrementalMarginUsd>.15m);Assert.Equal(3,risk.Valuation.CrossPortfolio!.ScenarioCount);
        Assert.True(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk,0,0,0,Now));
        var favorable=o with{Scenarios=o.Scenarios.Select(x=>x with{Capital=o.Before}).ToArray()};
        var lower=DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy(),favorable,Now)!;
        Assert.True(lower.Valuation.IncrementalMarginUsd>0);Assert.True(risk.Valuation.IncrementalMarginUsd>lower.Valuation.IncrementalMarginUsd);
    }

    [Theory]
    [InlineData("missing-scenario")][InlineData("duplicate-scenario")][InlineData("partial-pending")][InlineData("cash-drift")]
    [InlineData("unknown-collateral")][InlineData("missing-collateral")][InlineData("mode")][InlineData("native-usd-missing")]
    [InlineData("future-capital")][InlineData("stale")][InlineData("future")][InlineData("epoch")][InlineData("scope")]
    [InlineData("loss-gap")][InlineData("loss-currency")][InlineData("negative-loss")][InlineData("loss-fx")]
    [InlineData("wrong-unit")][InlineData("too-large")][InlineData("non-postonly")][InlineData("near-market")]
    [InlineData("wrong-product")][InlineData("linear")][InlineData("contract-size")][InlineData("duplicate-market")]
    [InlineData("price-grid")][InlineData("price-band")][InlineData("fee")][InlineData("wrong-policy")]
    [InlineData("cap-expanded")][InlineData("inventory-flags")][InlineData("unknown-position")][InlineData("overflow")]
    public void IncompleteOrIneligibleEvidenceFailsClosed(string problem)
    {
        var b=Baseline();var c=Command();var p=Policy();var o=Observation(b);
        switch(problem)
        {
            case "missing-scenario":o=o with{Scenarios=o.Scenarios.Take(2).ToArray()};break;
            case "duplicate-scenario":o=o with{Scenarios=[o.Scenarios[0],o.Scenarios[0],o.Scenarios[2]]};break;
            case "partial-pending":o=o with{Pending=[new("ETH-PERPETUAL",1,1750)]};break;
            case "cash-drift":o=o with{Balances=o.Balances.Select(x=>x.Currency=="ETH"?x with{CashBalance=.005m}:x).ToArray()};break;
            case "unknown-collateral":o=o with{Balances=o.Balances.Select(x=>x.Currency=="BUIDL"?x with{Equity=.1m}:x).ToArray()};break;
            case "missing-collateral":o=o with{Balances=o.Balances.Take(2).ToArray()};break;
            case "mode":o=o with{Before=o.Before with{MarginModel="segregated_pm"}};break;
            case "native-usd-missing":o=o with{After=o.After with{InitialMarginUsd=-1}};break;
            case "future-capital":o=o with{Before=o.Before with{ObservedUtc=Now.AddSeconds(1)}};break;
            case "stale":o=o with{StartedUtc=Now.AddSeconds(-6)};break;
            case "future":o=o with{CompletedUtc=Now.AddSeconds(1)};break;
            case "epoch":o=o with{Epoch=Guid.NewGuid()};break;
            case "scope":o=o with{Scope=new(o.Scope.Tenant,o.Scope.Venue,o.Scope.Environment,new("other"),o.Scope.Subaccount)};break;
            case "loss-gap":o=o with{LossThroughUtc=Now.AddSeconds(-1)};break;
            case "loss-currency":o=o with{DailyDebits=o.DailyDebits.Take(4).ToArray()};break;
            case "negative-loss":o=o with{DailyDebits=o.DailyDebits.Select(x=>x with{NegativeCashflowBound=-1}).ToArray()};break;
            case "loss-fx":o=o with{DailyDebits=o.DailyDebits.Select(x=>x.Currency=="USDT"?x with{NegativeCashflowBound=1}:x).ToArray()};break;
            case "wrong-unit":c=c with{Payload=c.Payload with{QuantityUnit=OmsQuantityUnit.BaseAsset}};break;
            case "too-large":c=c with{Payload=c.Payload with{Quantity=2}};break;
            case "non-postonly":c=c with{Payload=c.Payload with{PostOnly=false}};break;
            case "near-market":c=c with{Payload=c.Payload with{LimitPrice=1900}};break;
            case "wrong-product":c=c with{Payload=c.Payload with{Instrument="BTC-PERPETUAL"}};break;
            case "linear":o=o with{Instruments=[o.Instruments[0] with{Terms=o.Instruments[0].Terms with{InstrumentType="linear"}}]};break;
            case "contract-size":o=o with{Instruments=[o.Instruments[0] with{ContractSize=10}]};break;
            case "duplicate-market":o=o with{Instruments=[o.Instruments[0],o.Instruments[0]]};break;
            case "price-grid":c=c with{Payload=c.Payload with{LimitPrice=1750.1m}};break;
            case "price-band":o=o with{MinPrice=1600,MaxPrice=1700};break;
            case "fee":o=o with{FeeBoundEth=-1};break;
            case "wrong-policy":p=p with{CrossPortfolio=null};break;
            case "cap-expanded":p=p with{Limits=p.Limits with{MaxOrderUsd=2}};break;
            case "inventory-flags":o=o with{Balances=o.Balances.Select(x=>x with{PortfolioMarginingEnabled=false}).ToArray()};break;
            case "unknown-position":b=b with{Positions=[new("UNKNOWN","future","buy",1,"USD",.01m,"ETH")]};break;
            case "overflow":o=o with{FeeBoundEth=decimal.MaxValue};break;
        }
        Assert.Null(DeribitCrossPortfolioRiskV2.Evaluate(c,b,p,o,Now));
    }

    [Fact]
    public void OutstandingIntentExpandsBothDirectionsAndCannotReuseSmallerSimulation()
    {
        var b=Baseline();var c=Command();var o=Observation(b);o=o with{Pending=[new("ETH-PERPETUAL",1,1750)],
            Scenarios=Enumerable.Range(-2,5).Select(x=>new DeribitCrossPmScenarioV2(x,o.Before with{InitialMarginUsd=.2m+Math.Abs(x)*.2m})).ToArray()};
        var risk=DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy(),o,Now)!;Assert.NotNull(risk);
        Assert.Equal(2,risk.Valuation.CrossPortfolio!.ScenarioNativeUsd);Assert.Equal(5,risk.Valuation.CrossPortfolio.ScenarioCount);
        Assert.False(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk,2,0,2,Now));
        Assert.Null(DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy() with{CrossPortfolio=Policy().CrossPortfolio! with{MaximumScenarioNativeUsd=1}},o,Now));
    }

    [Fact]
    public void NativeBandsOnlyRejectTheSideThatWouldBeClampedAndPolicyStillBoundsPassiveDistance()
    {
        var b=Baseline();var c=Command();var o=Observation(b) with{MinPrice=1980,MaxPrice=2020};
        Assert.NotNull(DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy(),o,Now));
        var sell=c with{Payload=c.Payload with{Side=OrderSide.Sell,LimitPrice=2250}};
        Assert.NotNull(DeribitCrossPortfolioRiskV2.Evaluate(sell,b,Policy(),o,Now));
        Assert.Null(DeribitCrossPortfolioRiskV2.Evaluate(c with{Payload=c.Payload with{LimitPrice=1500}},b,Policy(),o,Now));
        Assert.Null(DeribitCrossPortfolioRiskV2.Evaluate(c,b,Policy() with{CrossPortfolio=Policy().CrossPortfolio! with{DailyLossMetric="all-account-daily-loss"}},o,Now));
    }

    [Fact]
    public void VersionOneSerializationRemainsByteCompatibleAndCrossEvidenceCannotEnterStandardLimits()
    {
        var p=DeribitRiskInputsTests.Policy();var b=DeribitRiskInputsTests.Baseline();var c=DeribitRiskInputsTests.Command();
        var old=DeribitRiskInputsReader.Evaluate(c,b,p,DeribitRiskInputsTests.Observation(b),Now)!;
        Assert.DoesNotContain("crossPortfolio",OmsRecoveryRunCodec.Encode(p).Json,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("crossPortfolio",OmsRecoveryRunCodec.Encode(old.Valuation).Json,StringComparison.OrdinalIgnoreCase);
        var policyJson=JsonNode.Parse(OmsRecoveryRunCodec.Encode(p).Json)!.AsObject();
        Assert.Equal(8,policyJson.Count);
        var pmBaseline=Baseline();var pm=DeribitCrossPortfolioRiskV2.Evaluate(Command(),pmBaseline,Policy(),Observation(pmBaseline),Now)!;
        Assert.False(OmsRiskLimitsV1.Allows(c,old.Valuation.StateSha256,old with{Valuation=old.Valuation with{CrossPortfolio=pm.Valuation.CrossPortfolio}},0,0,0,Now));
        Assert.False(OmsRiskLimitsV1.Allows(Command(),pm.Valuation.StateSha256,pm with{Valuation=pm.Valuation with{CrossPortfolio=null}},0,0,0,Now));
        Assert.Null(DeribitRiskInputsReader.Evaluate(c,b,Policy(),DeribitRiskInputsTests.Observation(b),Now));
    }

    [Fact]
    public async Task CurrentPendingAndPolicyAreRevalidatedBeforeCachedPmObservationCanBeUsed()
    {
        var b=Baseline();var c=Command();var policy=new Policies();var pending=new Pending();var native=new Observations(Observation(b));
        var reader=new DeribitRiskInputsReader(policy,new Standard(),new Clock(),pending,native);
        Assert.NotNull(await reader.ReadAsync(c,b));Assert.NotNull(await reader.ReadAsync(c,b));Assert.Equal(1,native.Calls);
        policy.Value=Policy() with{Limits=Policy().Limits with{MaxMarginUsd=.01m}};
        var changed=(await reader.ReadAsync(c,b))!;Assert.False(OmsRiskLimitsV1.Allows(c,changed.Valuation.StateSha256,changed,0,0,0,Now));
        Assert.Equal(1,native.Calls);
        pending.Value=[new("ETH-PERPETUAL",1,1750)];Assert.Null(await reader.ReadAsync(c,b));Assert.Equal(2,native.Calls);
        Assert.Null(await new DeribitRiskInputsReader(policy,new Standard(),new Clock(),pending).ReadAsync(c,b));
    }
    private sealed class Policies:IOmsRiskPolicyReader
    {internal OmsRiskPolicyDocumentV1 Value=Policy();public Task<OmsRiskPolicyDocumentV1?> ReadAsync(TradeOps.Domain.Oms.AccountKey s,CancellationToken ct=default)=>Task.FromResult<OmsRiskPolicyDocumentV1?>(Value);}
    private sealed class Pending:IOmsPendingRiskReader
    {internal IReadOnlyList<OmsPendingNativeRiskV1> Value=[];public Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(TradeOps.Domain.Oms.AccountKey s,TradeOps.Domain.Oms.OmsCommandId c,CancellationToken ct=default)=>Task.FromResult(Value);}
    private sealed class Standard:IDeribitRiskObservationReader
    {public Task<DeribitRiskObservationV1?> ReadAsync(OmsJournalCommandRequest c,OmsRecoveredAccountStateV1 b,CancellationToken ct=default)=>throw new Exception("PM must never use standard margin observations.");}
    private sealed class Observations(DeribitCrossPmRiskObservationV2 value):IDeribitCrossPortfolioRiskObservationReaderV2
    {internal int Calls;public Task<DeribitCrossPmRiskObservationV2?> ReadAsync(OmsJournalCommandRequest c,OmsRecoveredAccountStateV1 b,CancellationToken ct=default){Calls++;return Task.FromResult<DeribitCrossPmRiskObservationV2?>(value);}}
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
}
