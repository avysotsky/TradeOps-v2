using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitRiskInputsTests
{
    internal static readonly DateTimeOffset Now=new(2026,11,6,12,0,0,TimeSpan.Zero);
    internal static OmsJournalCommandRequest Command()=>DeribitCommandReceiptFixtures.Command() with { Payload=DeribitCommandReceiptFixtures.Command().Payload with { Quantity=10,TargetClientOrderId="risk-native-label" } };
    internal static OmsRecoveredAccountStateV1 Baseline()=>new(1,Command().Scope,Guid.NewGuid(),Guid.NewGuid(),DeribitRecoveryCoherencePolicy.Policy,Now,Now.AddMinutes(-5),Now,
        [],[],[new("ETH-PERPETUAL","future","sell",-100,"USD",-.05m,"ETH")],[new("ETH",1,1,false,false,new(Now,Now))],[],[],Now.AddSeconds(5));
    internal static OmsRiskPolicyDocumentV1 Policy()=>new(1,new(1,Command().Scope,"1",100,1000,10000,20,2000,10000,500,100,5),["ETH-PERPETUAL"],Now.AddMinutes(-1),Now.AddHours(1),2,1.5m,.2m);
    internal static DeribitRiskObservationV1 Observation(OmsRecoveredAccountStateV1 b)=>new(b.Scope,b.Epoch,Now,Now,Now.Date,Now,
        [new("ETH-PERPETUAL","future","reversed","ETH","USD","ETH",true,Now.AddYears(5),1,.5m,[],2000,2000)],
        [new("ETH",1,1,1,.88m,false,false,.1m,.12m,-.01m,-.02m,.03m,2000)],.01m,.02m,.001m,1700,2300,[new("ETH-PERPETUAL","ETH",10,.01m,.02m,.001m)]);

    [Fact]
    public void NativeUsdNotionalNeverMultipliesByUsdPriceAndSegregatedCollateralUsesStressBounds()
    {
        var b=Baseline();var c=Command() with { Payload=Command().Payload with { ReduceOnly=true } };
        var risk=DeribitRiskInputsReader.Evaluate(c,b,Policy(),Observation(b),Now)!;Assert.NotNull(risk);
        Assert.Equal(10,risk.Valuation.OrderNotionalUsd);Assert.Equal(20,risk.Valuation.PositionDeltaUsd);Assert.Equal(4110,risk.Valuation.PortfolioGrossUsd);
        Assert.Equal(1000,risk.Valuation.EquityUsd);Assert.Equal(480,risk.Valuation.MarginUsedUsd);
        Assert.Equal(126,risk.Valuation.IncrementalMarginUsd);Assert.Equal(240,risk.Valuation.DailyLossUsd);
        Assert.True(risk.Valuation.ReduceOnlyVerified);Assert.True(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk,0,0,0,Now));
    }
    [Theory]
    [InlineData("expired-policy")] [InlineData("scope")] [InlineData("stale")] [InlineData("future")] [InlineData("loss-gap")]
    [InlineData("linear")] [InlineData("option")] [InlineData("unknown-position")] [InlineData("unit")] [InlineData("cross")]
    [InlineData("portfolio-margin")] [InlineData("balance-drift")] [InlineData("inactive")] [InlineData("expired-instrument")]
    [InlineData("amount-step")] [InlineData("price-step")] [InlineData("price-band")] [InlineData("currency-margin")]
    [InlineData("missing-fx")] [InlineData("duplicate-market")] [InlineData("unsupported-order")] [InlineData("eligibility")]
    [InlineData("negative-margin")] [InlineData("bad-tick-step")] [InlineData("overflow")] [InlineData("label")]
    public void InvalidNativeEvidenceCannotCreateValuation(string problem)
    {
        var b=Baseline();var c=Command();var p=Policy();var o=Observation(b);
        switch(problem)
        {
            case "expired-policy":p=p with { ValidThroughUtc=Now.AddSeconds(-1) };break;
            case "scope":o=o with { Epoch=Guid.NewGuid() };break;
            case "stale":o=o with { StartedUtc=Now.AddSeconds(-6) };break;
            case "future":o=o with { CompletedUtc=Now.AddSeconds(1) };break;
            case "loss-gap":o=o with { LossThroughUtc=Now.AddSeconds(-1) };break;
            case "linear":o=o with { Instruments=[o.Instruments[0] with { InstrumentType="linear" }] };break;
            case "option":o=o with { Instruments=[o.Instruments[0] with { Kind="option" }] };break;
            case "unknown-position":b=b with { Positions=[b.Positions[0] with { NativeInstrument="UNKNOWN" }] };break;
            case "unit":b=b with { Positions=[b.Positions[0] with { SizeCurrency="ETH" }] };break;
            case "cross":o=o with { Currencies=[o.Currencies[0] with { CrossCollateral=true }] };break;
            case "portfolio-margin":o=o with { Currencies=[o.Currencies[0] with { PortfolioMargin=true }] };break;
            case "balance-drift":o=o with { Currencies=[o.Currencies[0] with { Balance=2 }] };break;
            case "inactive":o=o with { Instruments=[o.Instruments[0] with { Active=false }] };break;
            case "expired-instrument":o=o with { Instruments=[o.Instruments[0] with { ExpiresUtc=Now }] };break;
            case "amount-step":o=o with { Instruments=[o.Instruments[0] with { AmountStep=3 }] };break;
            case "price-step":o=o with { Instruments=[o.Instruments[0] with { PriceStep=3 }] };break;
            case "price-band":o=o with { MaxPrice=1999 };break;
            case "currency-margin":o=o with { BuyMargin=2 };break;
            case "missing-fx":o=o with { Currencies=[o.Currencies[0] with { Fx=0 }] };break;
            case "duplicate-market":o=o with { Instruments=[o.Instruments[0],o.Instruments[0]] };break;
            case "unsupported-order":c=c with { Payload=c.Payload with { Kind=OmsJournalCommandKind.Replace,TargetExchangeOrderId="native" } };break;
            case "eligibility":p=p with { AllowedInstruments=[] };break;
            case "negative-margin":o=o with { BuyMargin=-1 };break;
            case "bad-tick-step":o=o with { Instruments=[o.Instruments[0] with { PriceSteps=[new(1000,.3m)] }] };break;
            case "overflow":o=o with { Currencies=[o.Currencies[0] with { Fx=decimal.MaxValue }] };break;
            case "label":c=c with{Payload=c.Payload with{TargetClientOrderId=new string('X',65)}};break;
        }
        Assert.Null(DeribitRiskInputsReader.Evaluate(c,b,p,o,Now));
    }
    [Fact]
    public async Task CachedNativeObservationsNeverCachePolicyApprovalOrOutliveFreshness()
    {
        var b=Baseline();var c=Command();var policies=new Policies();var native=new Observations(b);var clock=new Clock();
        var pending=new Pending();var reader=new DeribitRiskInputsReader(policies,native,clock,pending);
        var first=(await reader.ReadAsync(c,b))!;Assert.NotNull(first);
        policies.Value=Policy() with { Limits=Policy().Limits with { MaxOrderUsd=1,Revision="2" } };
        var changed=(await reader.ReadAsync(c,b))!;Assert.Equal(1,native.Calls);
        Assert.False(OmsRiskLimitsV1.Allows(c,changed.Valuation.StateSha256,changed,0,0,0,Now));
        pending.Value=[new("ETH-PERPETUAL",20,2000)];Assert.Null(await reader.ReadAsync(c,b));Assert.Equal(2,native.Calls);
        clock.Now=Now.AddSeconds(6);Assert.Null(await reader.ReadAsync(c,b));Assert.Equal(3,native.Calls);
        policies.Value=null;await reader.ReadAsync(c,b);Assert.Equal(3,native.Calls);
    }
    private sealed class Policies:IOmsRiskPolicyReader
    { internal OmsRiskPolicyDocumentV1? Value=Policy();public Task<OmsRiskPolicyDocumentV1?> ReadAsync(TradeOps.Domain.Oms.AccountKey s,CancellationToken ct=default)=>Task.FromResult(Value); }
    private sealed class Observations(OmsRecoveredAccountStateV1 b):IDeribitRiskObservationReader
    { internal int Calls;public Task<DeribitRiskObservationV1?> ReadAsync(OmsJournalCommandRequest c,OmsRecoveredAccountStateV1 s,CancellationToken ct=default) { Calls++;return Task.FromResult<DeribitRiskObservationV1?>(Observation(b)); } }
    private sealed class Clock:TimeProvider { internal DateTimeOffset Now=DeribitRiskInputsTests.Now;public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Pending:IOmsPendingRiskReader
    {internal IReadOnlyList<OmsPendingNativeRiskV1> Value=[];public Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(TradeOps.Domain.Oms.AccountKey scope,TradeOps.Domain.Oms.OmsCommandId except,CancellationToken ct=default)=>Task.FromResult(Value);}
    [Fact]
    public void NonlinearCumulativeMarginCannotBeUnderreservedByEvaluatingIsolatedOrders()
    {
        var b=Baseline();var c=Command();var o=Observation(b) with{BuyMargin=.05m,SellMargin=.09m,FeeMargin=.003m,
            MarginBounds=[new("ETH-PERPETUAL","ETH",30,.05m,.09m,.003m)]};
        var risk=DeribitRiskInputsReader.Evaluate(c,b,Policy(),o,Now)!;Assert.NotNull(risk);Assert.Equal(558,risk.Valuation.IncrementalMarginUsd);
        Assert.False(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk,20,0,1,Now));
    }
}
