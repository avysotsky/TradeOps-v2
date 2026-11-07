using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record OmsCrossPortfolioPolicyV2(int SchemaVersion,string MarginModel,IReadOnlyList<string> AllowedCollateralCurrencies,
    int MaximumScenarioNativeUsd,decimal MinimumDistanceFraction,string DailyLossMetric);
public sealed record OmsCrossPortfolioEvidenceV2(int SchemaVersion,string MarginModel,string ObservationSha256,string PolicySha256,int ScenarioNativeUsd,int ScenarioCount)
{
    internal static bool Valid(OmsCrossPortfolioEvidenceV2? value)=>value is{SchemaVersion:2,MarginModel:"cross_pm",ScenarioNativeUsd:>=1 and <=3}&&
        value.ScenarioCount==2*value.ScenarioNativeUsd+1&&Hash(value.ObservationSha256)&&Hash(value.PolicySha256);
    private static bool Hash(string? value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}
public sealed record DeribitCrossPmCapitalV2(string Currency,string MarginModel,bool CrossCollateral,bool PortfolioMargin,
    decimal EquityUsd,decimal MarginBalanceUsd,decimal InitialMarginUsd,decimal MaintenanceMarginUsd,decimal ProjectedInitialMarginEth,DateTimeOffset ObservedUtc);
public sealed record DeribitCrossPmScenarioV2(decimal NativeUsdChange,DeribitCrossPmCapitalV2 Capital);
public sealed record DeribitCrossPmInstrumentV2(DeribitRiskInstrumentV1 Terms,decimal ContractSize,decimal BestBid,decimal BestAsk,DateTimeOffset ObservedUtc);
public sealed record DeribitCrossPmDebitV2(string Currency,decimal NegativeCashflowBound,decimal NegativeSessionPnlBound);
public sealed record DeribitCrossPmRiskObservationV2(int SchemaVersion,AccountKey Scope,Guid Epoch,DateTimeOffset StartedUtc,DateTimeOffset CompletedUtc,
    DateTimeOffset LossFromUtc,DateTimeOffset LossThroughUtc,IReadOnlyList<DeribitCrossPmInstrumentV2> Instruments,IReadOnlyList<DeribitCurrencyBalance> Balances,
    DeribitCrossPmCapitalV2 Before,DeribitCrossPmCapitalV2 After,IReadOnlyList<DeribitCrossPmScenarioV2> Scenarios,
    IReadOnlyList<OmsPendingNativeRiskV1> Pending,IReadOnlyList<DeribitCrossPmDebitV2> DailyDebits,IReadOnlyDictionary<string,decimal> FxUsd,
    decimal FeeBoundEth,decimal MinPrice,decimal MaxPrice);
public interface IDeribitCrossPortfolioRiskObservationReaderV2
{Task<DeribitCrossPmRiskObservationV2?> ReadAsync(OmsJournalCommandRequest command,OmsRecoveredAccountStateV1 baseline,CancellationToken ct=default);}

public static class DeribitCrossPortfolioRiskV2
{
    public static bool PolicyValid(OmsRiskPolicyDocumentV1 p)=>p.SchemaVersion==2&&p.Limits.SchemaVersion==2&&p.CrossPortfolio is{SchemaVersion:2,MarginModel:"cross_pm",MaximumScenarioNativeUsd:>=1 and <=3} x&&
        x.DailyLossMetric=="listed-derivatives-settlement-debits-v2"&&x.MinimumDistanceFraction is >=.1m and <1&&x.AllowedCollateralCurrencies is{Count:>0 and <=5}&&x.AllowedCollateralCurrencies.Distinct(StringComparer.Ordinal).Count()==x.AllowedCollateralCurrencies.Count&&
        x.AllowedCollateralCurrencies.All(c=>c is "BTC" or "ETH" or "USDC" or "USDT" or "EURR")&&p.AllowedInstruments.SequenceEqual(new[]{"ETH-PERPETUAL"})&&
        p.Limits.MaxOrderUsd is >0 and <=1&&p.Limits.MaxPendingUsd is >0 and <=2&&p.Limits.MaxPendingCommands is >0 and <=2&&
        p.FxStressFactor is >=1 and <=3&&p.MarginMultiplier is >=1 and <=10&&p.MaxLimitDeviationFraction>=x.MinimumDistanceFraction&&p.MaxLimitDeviationFraction<1;
    public static OmsRiskInputsV1? Evaluate(OmsJournalCommandRequest command,OmsRecoveredAccountStateV1 baseline,OmsRiskPolicyDocumentV1 policy,DeribitCrossPmRiskObservationV2 input,DateTimeOffset now)
    {
        try
        {
            command.Validate();var p=command.Payload;
            if(!PolicyValid(policy)||command.Scope!=baseline.Scope||command.Scope!=policy.Limits.Scope||input.Scope!=baseline.Scope||input.Epoch!=baseline.Epoch||input.SchemaVersion!=2||
                command.Scope.Venue.Value!="DERIBIT"||p.Instrument!="ETH-PERPETUAL"||p.Kind is not(OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace)||
                p.Kind==OmsJournalCommandKind.Place&&command.ExpectedAggregateVersion.Value!=0||p.Kind==OmsJournalCommandKind.Replace&&command.ExpectedAggregateVersion.Value<1||
                p.Quantity!=1||p.QuantityUnit!=OmsQuantityUnit.QuoteAsset||p.OrderType!=OrderType.Limit||p.LimitPrice is not{} price||price<=0||p.TriggerPrice is not null||!p.PostOnly||
                p.TimeInForce is not("GTC" or "good_til_cancelled")||p.FixOrigClOrdId is not null||p.TargetClientOrderId is null||
                now<policy.ValidFromUtc||now>policy.ValidThroughUtc||policy.ValidFromUtc.Offset!=TimeSpan.Zero||policy.ValidThroughUtc.Offset!=TimeSpan.Zero||now>baseline.UsableThroughUtc||
                input.StartedUtc.Offset!=TimeSpan.Zero||input.CompletedUtc.Offset!=TimeSpan.Zero||now<input.CompletedUtc||now-input.StartedUtc>TimeSpan.FromSeconds(5)||input.CompletedUtc<input.StartedUtc||
                input.LossFromUtc!=new DateTimeOffset(now.UtcDateTime.Date,TimeSpan.Zero)||input.LossThroughUtc.Offset!=TimeSpan.Zero||input.LossThroughUtc<input.LossFromUtc||
                input.LossThroughUtc<baseline.NativeThroughUtc||input.LossThroughUtc>input.CompletedUtc.AddSeconds(2)||input.LossThroughUtc.Date!=now.Date||
                input.Instruments.Count is <1 or >16||input.Balances.Count is <1 or >32||input.Pending.Count>2||input.FxUsd.Count is <1 or >32||input.FeeBoundEth<0)return null;
            var markets=input.Instruments.ToDictionary(x=>x.Terms.Name,StringComparer.Ordinal);
            if(markets.Count!=input.Instruments.Count||!markets.TryGetValue(p.Instrument,out var target))return null;
            foreach(var m in input.Instruments)
            {
                var t=m.Terms;
                if(t.Kind!="future"||t.InstrumentType!="reversed"||t.BaseCurrency!="ETH"||t.QuoteCurrency!="USD"||t.SettlementCurrency!="ETH"||!t.Active||t.ExpiresUtc<=now||
                    m.ContractSize!=1||t.AmountStep!=1||t.PriceStep<=0||t.Mark<=0||t.Index<=0||m.BestBid<=0||m.BestAsk<m.BestBid||
                    m.ObservedUtc.Offset!=TimeSpan.Zero||m.ObservedUtc<input.StartedUtc||m.ObservedUtc>input.CompletedUtc||t.PriceSteps.Count>32||
                    t.PriceSteps.Any(x=>x.Above<0||x.Step<=0||x.Step%t.PriceStep!=0)||t.PriceSteps.Select(x=>x.Above).Distinct().Count()!=t.PriceSteps.Count)return null;
            }
            var step=target.Terms.PriceSteps.OrderBy(x=>x.Above).Where(x=>price>x.Above).Select(x=>x.Step).DefaultIfEmpty(target.Terms.PriceStep).Last();
            // Native lower band clamps aggressive sells; upper band clamps aggressive buys.
            // Passive buys below min_price and passive sells above max_price are not clamped by these bands.
            if(price%step!=0||input.MinPrice<=0||input.MaxPrice<input.MinPrice||
                (p.Side==OrderSide.Buy?price>input.MaxPrice:price<input.MinPrice)||Math.Abs(price/target.Terms.Mark-1)>policy.MaxLimitDeviationFraction||
                (p.Side==OrderSide.Buy?price>target.BestBid*(1-policy.CrossPortfolio!.MinimumDistanceFraction):price<target.BestAsk*(1+policy.CrossPortfolio!.MinimumDistanceFraction)))return null;
            var expected=1m;var prices=new List<decimal>{price};
            foreach(var pending in input.Pending)
            {if(pending.Instrument!=p.Instrument||pending.NativeUsd!=1||pending.LimitPrice<=0)return null;expected+=pending.NativeUsd;prices.Add(pending.LimitPrice);}
            foreach(var order in baseline.Orders.Where(x=>x.State.NativeState=="open"))
            {var s=order.State;if(s.NativeInstrument!=p.Instrument||s.NativeAmount!=1||s.KnownFilledNativeAmount!=0||s.AmountUnit!=OmsPrivateAmountUnit.Usd||s.AmountCurrency!="USD"||order.LastReport.Price is not>0)return null;
                expected+=s.NativeAmount;prices.Add(order.LastReport.Price.Value);}
            if(expected>policy.CrossPortfolio.MaximumScenarioNativeUsd)return null;var radius=(int)expected;
            if(!input.Scenarios.Select(x=>x.NativeUsdChange).Order().SequenceEqual(Enumerable.Range(-radius,2*radius+1).Select(x=>(decimal)x)))return null;
            var capitals=input.Scenarios.Select(x=>x.Capital).Prepend(input.Before).Append(input.After).ToArray();
            if(capitals.Any(c=>c.Currency!="ETH"||c.MarginModel!="cross_pm"||!c.CrossCollateral||!c.PortfolioMargin||c.InitialMarginUsd<0||c.MaintenanceMarginUsd<0||c.MaintenanceMarginUsd>c.InitialMarginUsd||
                c.ProjectedInitialMarginEth<0||c.ObservedUtc.Offset!=TimeSpan.Zero||c.ObservedUtc<input.StartedUtc||c.ObservedUtc>input.CompletedUtc))return null;
            var balances=baseline.Balances.ToDictionary(x=>x.Currency,StringComparer.Ordinal);var actual=input.Balances.ToDictionary(x=>x.Currency,StringComparer.Ordinal);
            if(!balances.Keys.Order(StringComparer.Ordinal).SequenceEqual(actual.Keys.Order(StringComparer.Ordinal)))return null;
            decimal cashGross=0,concentration=0;
            foreach(var (currency,c) in actual)
            {
                var prior=balances[currency];if(c.Window is null||c.Window.RequestedAtUtc.Offset!=TimeSpan.Zero||c.Window.ReceivedAtUtc.Offset!=TimeSpan.Zero||
                    c.Window.RequestedAtUtc<input.StartedUtc||c.Window.ReceivedAtUtc<c.Window.RequestedAtUtc||c.Window.ReceivedAtUtc>input.CompletedUtc||
                    c.CashBalance!=prior.CashBalance||!c.CrossCollateralEnabled||!c.PortfolioMarginingEnabled||
                    c.CrossCollateralEnabled!=prior.CrossCollateralEnabled||c.PortfolioMarginingEnabled!=prior.PortfolioMarginingEnabled)return null;
                if(c.CashBalance==0&&c.Equity==0)continue;
                if(!policy.CrossPortfolio.AllowedCollateralCurrencies.Contains(currency,StringComparer.Ordinal)||!input.FxUsd.TryGetValue(currency,out var fx)||fx<=0)return null;
                var gross=Math.Max(Math.Abs(c.CashBalance),Math.Abs(c.Equity))*fx*policy.FxStressFactor;cashGross+=gross;if(currency=="ETH")concentration+=gross;
            }
            if(!input.FxUsd.TryGetValue("ETH",out var ethFx)||ethFx<=0)return null;var ethHigh=ethFx*policy.FxStressFactor;
            var currentMargin=Math.Max(Math.Max(input.Before.InitialMarginUsd,input.After.InitialMarginUsd),Math.Max(input.Before.ProjectedInitialMarginEth,input.After.ProjectedInitialMarginEth)*ethHigh);
            var worstMargin=capitals.Max(x=>Math.Max(x.InitialMarginUsd,x.ProjectedInitialMarginEth*ethHigh));
            var capital=capitals.Min(x=>Math.Min(x.EquityUsd,x.MarginBalanceUsd))/policy.FxStressFactor;
            var adverse=expected*prices.Max(x=>Math.Abs(target.Terms.Index/x-target.Terms.Index/target.Terms.Mark))*policy.FxStressFactor;
            var incremental=(Math.Max(0,worstMargin-currentMargin)+input.FeeBoundEth*ethHigh+adverse)*policy.MarginMultiplier;
            decimal grossUsd=cashGross,signed=0;
            foreach(var position in baseline.Positions)
            {
                if(!markets.TryGetValue(position.NativeInstrument,out var meta)||position.Kind!="future"||position.SizeCurrency!="USD"||position.BaseCurrency!="ETH"||
                    position.Direction is not("buy" or "sell" or "zero")||position.Direction=="buy"&&position.NativeSize<=0||position.Direction=="sell"&&position.NativeSize>=0||position.Direction=="zero"&&position.NativeSize!=0)return null;
                var size=Math.Abs(position.NativeSize)*Math.Max(1,meta.Terms.Index/meta.Terms.Mark)*policy.FxStressFactor;grossUsd+=size;concentration+=size;if(position.NativeInstrument==p.Instrument)signed+=position.NativeSize;
            }
            var delta=expected*Math.Max(1,target.Terms.Index/target.Terms.Mark)*policy.FxStressFactor;
            grossUsd+=Math.Max(0,delta-p.Quantity);concentration+=Math.Max(0,delta-p.Quantity);
            if(!input.DailyDebits.Select(x=>x.Currency).Order(StringComparer.Ordinal).SequenceEqual(new[]{"BTC","ETH","EURR","USDC","USDT"}))return null;
            decimal loss=0;foreach(var debit in input.DailyDebits)
            {if(debit.NegativeCashflowBound<0||debit.NegativeSessionPnlBound<0)return null;var native=debit.NegativeCashflowBound+debit.NegativeSessionPnlBound;if(native==0)continue;
                if(!input.FxUsd.TryGetValue(debit.Currency,out var fx)||fx<=0)return null;loss+=native*fx*policy.FxStressFactor;}
            var reduce=p.Side==OrderSide.Buy?signed<0&&p.Quantity<=-signed:signed>0&&p.Quantity<=signed;
            var proof=new OmsCrossPortfolioEvidenceV2(2,"cross_pm",Convert.ToHexString(OmsRecoveryRunCodec.Encode(input).Sha256),Convert.ToHexString(OmsRecoveryRunCodec.Encode(policy).Sha256),radius,input.Scenarios.Count);
            var value=new OmsRiskValuationV1(p.Kind==OmsJournalCommandKind.Replace?4:3,command.Scope,Convert.ToHexString(OmsRecoveryRunCodec.Encode(baseline).Sha256),Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256),
                input.StartedUtc,new[]{input.StartedUtc.AddSeconds(5),baseline.UsableThroughUtc,policy.ValidThroughUtc}.Min(),true,true,true,reduce,p.Quantity,delta,grossUsd,concentration,capital,currentMargin,incremental,loss,proof);
            return new(policy.Limits,value);
        }
        catch(Exception e)when(e is ArgumentException or OverflowException or InvalidOperationException){return null;}
    }
}
