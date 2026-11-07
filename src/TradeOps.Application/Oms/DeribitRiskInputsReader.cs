using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record OmsRiskPolicyDocumentV1(int SchemaVersion, OmsAccountRiskPolicyV1 Limits,
    IReadOnlyList<string> AllowedInstruments, DateTimeOffset ValidFromUtc, DateTimeOffset ValidThroughUtc,
    decimal FxStressFactor, decimal MarginMultiplier, decimal MaxLimitDeviationFraction,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OmsCrossPortfolioPolicyV2? CrossPortfolio=null);
public interface IOmsRiskPolicyReader
{
    Task<OmsRiskPolicyDocumentV1?> ReadAsync(AccountKey scope, CancellationToken ct = default);
}
public sealed record DeribitRiskInstrumentV1(string Name, string Kind, string InstrumentType, string BaseCurrency,
    string QuoteCurrency, string SettlementCurrency, bool Active, DateTimeOffset ExpiresUtc,
    decimal AmountStep, decimal PriceStep, IReadOnlyList<DeribitRiskPriceStepV1> PriceSteps, decimal Mark, decimal Index);
public sealed record DeribitRiskPriceStepV1(decimal Above, decimal Step);
public sealed record DeribitRiskCurrencyV1(string Currency, decimal Balance, decimal Equity,decimal MarginBalance,decimal AvailableFunds, bool CrossCollateral,
    bool PortfolioMargin, decimal InitialMargin, decimal ProjectedInitialMargin, decimal SessionRpl, decimal SessionUpl,
    decimal DailyDebitBound, decimal Fx);
public sealed record OmsPendingNativeRiskV1(string Instrument,decimal NativeUsd,decimal LimitPrice);
public interface IOmsPendingRiskReader
{Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(AccountKey scope,OmsCommandId except,CancellationToken ct=default);}
public sealed record DeribitRiskMarginBoundV1(string Instrument,string Currency,decimal NativeUsd,decimal Buy,decimal Sell,decimal Fee);
public sealed record DeribitRiskObservationV1(AccountKey Scope, Guid Epoch, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, DateTimeOffset LossFromUtc, DateTimeOffset LossThroughUtc,
    IReadOnlyList<DeribitRiskInstrumentV1> Instruments, IReadOnlyList<DeribitRiskCurrencyV1> Currencies,
    decimal BuyMargin, decimal SellMargin, decimal FeeMargin, decimal MinPrice, decimal MaxPrice,
    IReadOnlyList<DeribitRiskMarginBoundV1>? MarginBounds=null);
public interface IDeribitRiskObservationReader
{
    Task<DeribitRiskObservationV1?> ReadAsync(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default);
}

/// <summary>Supported policy: standard segregated BTC/ETH inverse futures. Unknown products,
/// margin modes, units, incomplete daily loss traversal or missing policy return no valuation.</summary>
public sealed class DeribitRiskInputsReader(IOmsRiskPolicyReader policies, IDeribitRiskObservationReader observations,
    TimeProvider? clock = null,IOmsPendingRiskReader? pendingRisk=null,
    IDeribitCrossPortfolioRiskObservationReaderV2? crossPortfolio=null) : IOmsRiskInputsReader
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,DeribitRiskObservationV1> cache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,DeribitCrossPmRiskObservationV2> crossCache = new();
    private readonly object cacheSync=new();
    public async Task<OmsRiskInputsV1?> ReadAsync(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default)
    {
        var policy = await policies.ReadAsync(command.Scope, ct); if (policy is null) return null;
        var now=(clock ?? TimeProvider.System).GetUtcNow();
        foreach(var old in cache.Where(x => now<x.Value.StartedUtc || now-x.Value.StartedUtc>TimeSpan.FromSeconds(5)).ToArray()) cache.TryRemove(old.Key,out _);
        var pending=pendingRisk is null?null:await pendingRisk.ReadAsync(command.Scope,command.CommandId,ct);
        var key=Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256)+":"+Convert.ToHexString(OmsRecoveryRunCodec.Encode(baseline).Sha256)+":"+
            (pending is null?Guid.NewGuid().ToString("N"):Convert.ToHexString(OmsRecoveryRunCodec.Encode(pending.OrderBy(x=>x.Instrument,StringComparer.Ordinal).ThenBy(x=>x.LimitPrice).ThenBy(x=>x.NativeUsd).ToArray()).Sha256));
        if(policy.SchemaVersion==2)
        {
            if(crossPortfolio is null||pending is null||!DeribitCrossPortfolioRiskV2.PolicyValid(policy))return null;
            foreach(var old in crossCache.Where(x=>now<x.Value.StartedUtc||now-x.Value.StartedUtc>TimeSpan.FromSeconds(5)).ToArray())crossCache.TryRemove(old.Key,out _);
            if(!crossCache.TryGetValue(key,out var observed))
            {
                observed=await crossPortfolio.ReadAsync(command,baseline,ct);if(observed is null)return null;
                static string Hash(IReadOnlyList<OmsPendingNativeRiskV1> rows)=>Convert.ToHexString(OmsRecoveryRunCodec.Encode(rows.OrderBy(x=>x.Instrument,StringComparer.Ordinal).ThenBy(x=>x.LimitPrice).ThenBy(x=>x.NativeUsd).ToArray()).Sha256);
                if(Hash(pending)!=Hash(observed.Pending))return null;
                lock(cacheSync)if(crossCache.Count<128)crossCache.TryAdd(key,observed);
            }
            return DeribitCrossPortfolioRiskV2.Evaluate(command,baseline,policy,observed,(clock??TimeProvider.System).GetUtcNow());
        }
        if (!cache.TryGetValue(key,out var native))
        {
            native=await observations.ReadAsync(command,baseline,ct); if(native is null) return null;
            if(pending is not null)lock(cacheSync)if(cache.Count<128)cache.TryAdd(key,native);
        }
        if(pending is not null)
        {
            var expected=new Dictionary<string,decimal>(StringComparer.Ordinal);
            foreach(var row in pending)
            {if(row.NativeUsd<=0)return null;expected[row.Instrument]=expected.GetValueOrDefault(row.Instrument)+row.NativeUsd;}
            expected[command.Payload.Instrument]=expected.GetValueOrDefault(command.Payload.Instrument)+command.Payload.Quantity;
            foreach(var open in baseline.Orders.Where(x=>x.State.NativeState=="open"))
            {
                if(open.State.KnownFilledNativeAmount is not{} filled||filled<0||filled>open.State.NativeAmount)return null;
                expected[open.State.NativeInstrument]=expected.GetValueOrDefault(open.State.NativeInstrument)+open.State.NativeAmount-filled;
            }
            if(native.MarginBounds is null||expected.Any(x=>native.MarginBounds.Where(b=>b.Instrument==x.Key).Sum(b=>b.NativeUsd)<x.Value))return null;
        }
        return Evaluate(command, baseline, policy, native, (clock ?? TimeProvider.System).GetUtcNow());
    }

    public static OmsRiskInputsV1? Evaluate(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline,
        OmsRiskPolicyDocumentV1 policy, DeribitRiskObservationV1 input, DateTimeOffset now)
    {
        try
        {
            command.Validate(); var p = command.Payload;
            if (command.Scope != baseline.Scope || command.Scope != policy.Limits.Scope || input.Scope != baseline.Scope || input.Epoch != baseline.Epoch ||
                command.Scope.Venue.Value != "DERIBIT" || p.Kind is not(OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace) ||
                (p.Kind==OmsJournalCommandKind.Place?command.ExpectedAggregateVersion.Value!=0:command.ExpectedAggregateVersion.Value<1) ||
                p.OrderType != OrderType.Limit || p.LimitPrice is not { } price || p.TriggerPrice is not null ||
                p.QuantityUnit != OmsQuantityUnit.QuoteAsset || p.TimeInForce is not ("GTC" or "good_til_cancelled") ||
                p.TargetClientOrderId is null ||p.TargetClientOrderId.Length>64||p.TargetClientOrderId.Any(char.IsControl)|| (p.Kind==OmsJournalCommandKind.Place?p.TargetExchangeOrderId is not null:p.TargetExchangeOrderId is null) || p.FixOrigClOrdId is not null ||
                policy.SchemaVersion != 1 || policy.Limits.SchemaVersion != 1 || policy.CrossPortfolio is not null || policy.FxStressFactor < 1 || policy.MarginMultiplier < 1 ||
                policy.MaxLimitDeviationFraction is <= 0 or >= 1 || now < policy.ValidFromUtc || now > policy.ValidThroughUtc ||
                policy.ValidFromUtc.Offset != TimeSpan.Zero || policy.ValidThroughUtc.Offset != TimeSpan.Zero ||
                !policy.AllowedInstruments.Contains(p.Instrument, StringComparer.Ordinal) || policy.AllowedInstruments.Distinct().Count() != policy.AllowedInstruments.Count ||
                input.StartedUtc.Offset != TimeSpan.Zero || input.CompletedUtc.Offset != TimeSpan.Zero || input.CompletedUtc < input.StartedUtc ||
                now < input.CompletedUtc || now - input.StartedUtc > TimeSpan.FromSeconds(5) || now > baseline.UsableThroughUtc ||
                input.LossFromUtc != new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) || input.LossThroughUtc < baseline.NativeThroughUtc ||
                input.LossThroughUtc > input.CompletedUtc.AddSeconds(2) || input.LossThroughUtc.Date != now.Date ||
                input.Instruments.Count is < 1 or > 256 || input.Currencies.Count is < 1 or > 32 ||
                input.Instruments.Select(x => x.Name).Distinct().Count() != input.Instruments.Count ||
                input.Currencies.Select(x => x.Currency).Distinct().Count() != input.Currencies.Count) return null;
            var instruments = input.Instruments.ToDictionary(x => x.Name, StringComparer.Ordinal);
            foreach (var m in instruments.Values)
                if (m.Kind != "future" || m.InstrumentType != "reversed" || m.BaseCurrency is not ("BTC" or "ETH") ||
                    m.QuoteCurrency != "USD" || m.SettlementCurrency != m.BaseCurrency || !m.Active || m.ExpiresUtc <= now ||
                    m.AmountStep <= 0 || m.PriceStep <= 0 || m.Mark <= 0 || m.Index <= 0 ||
                    m.PriceSteps.Count > 32 || m.PriceSteps.Any(x => x.Above < 0 || x.Step <= 0 || x.Step % m.PriceStep != 0) ||
                    m.PriceSteps.Select(x => x.Above).Distinct().Count() != m.PriceSteps.Count) return null;
            if (!instruments.TryGetValue(p.Instrument, out var target)) return null;
            var step = target.PriceSteps.Where(x => price >= x.Above).OrderBy(x => x.Above).Select(x => x.Step).LastOrDefault(target.PriceStep);
            if (p.Quantity % target.AmountStep != 0 || price % step != 0 || input.MinPrice <= 0 || input.MaxPrice < input.MinPrice ||
                price < input.MinPrice || price > input.MaxPrice || Math.Abs(price / target.Mark - 1) > policy.MaxLimitDeviationFraction ||
                input.BuyMargin < 0 || input.SellMargin < 0 || input.FeeMargin < 0) return null;
            var balances = baseline.Balances.ToDictionary(x => x.Currency, StringComparer.Ordinal);
            if (!balances.Keys.Order().SequenceEqual(input.Currencies.Select(x => x.Currency).Order())) return null;
            decimal equity = 0, margin = 0, loss = 0,cashGross=0,cashConcentration=0; DeribitRiskCurrencyV1? collateral = null;
            var collateralBounds=new Dictionary<string,decimal>(StringComparer.Ordinal);
            foreach (var c in input.Currencies)
            {
                var b = balances[c.Currency];
                if (c.Currency is not ("BTC" or "ETH") || c.Fx <= 0 || c.InitialMargin < 0 || c.ProjectedInitialMargin < 0 ||
                    c.DailyDebitBound < 0 || c.CrossCollateral || c.PortfolioMargin || b.CrossCollateralEnabled || b.PortfolioMarginingEnabled || c.Balance != b.CashBalance) return null;
                var fxHigh = c.Fx * policy.FxStressFactor; var fxLow = c.Fx / policy.FxStressFactor;
                // Segregated collateral: portfolio USD budget is additional, not a replacement for target-currency margin availability.
                var availableBound=Math.Min(c.Equity,Math.Min(c.MarginBalance,Math.Max(c.InitialMargin,c.ProjectedInitialMargin)+c.AvailableFunds));
                collateralBounds[c.Currency]=availableBound;
                equity += availableBound >= 0 ? availableBound * fxLow : availableBound * fxHigh;
                var cashBound=new[]{Math.Abs(c.Balance),Math.Abs(c.Equity),Math.Abs(c.MarginBalance)}.Max()*fxHigh;
                cashGross+=cashBound;if(c.Currency==target.BaseCurrency)cashConcentration+=cashBound;
                margin += Math.Max(c.InitialMargin, c.ProjectedInitialMargin) * fxHigh;
                loss += (c.DailyDebitBound + Math.Max(0, -c.SessionRpl) + Math.Max(0, -c.SessionUpl)) * fxHigh;
                if (c.Currency == target.SettlementCurrency) collateral = c;
            }
            if (collateral is null || input.MarginBounds is null || input.MarginBounds.Count is <1 or >256 ||
                input.MarginBounds.Select(x=>x.Instrument).Distinct().Count()!=input.MarginBounds.Count) return null;
            equity = Math.Min(equity, collateralBounds[collateral.Currency] * collateral.Fx / policy.FxStressFactor);
            decimal gross = cashGross, concentration = cashConcentration, signedPosition = 0;
            foreach (var position in baseline.Positions)
            {
                if (!instruments.TryGetValue(position.NativeInstrument, out var meta) || position.Kind != "future" || position.SizeCurrency != "USD" ||
                    position.BaseCurrency != meta.BaseCurrency || position.Direction is not ("buy" or "sell" or "zero") ||
                    position.Direction == "buy" && position.NativeSize <= 0 || position.Direction == "sell" && position.NativeSize >= 0 ||
                    position.Direction == "zero" && position.NativeSize != 0) return null;
                var amount = Math.Abs(position.NativeSize)*Math.Max(1,meta.Index/meta.Mark); gross += amount;
                if(meta.BaseCurrency==target.BaseCurrency)concentration+=amount;
                if (position.NativeInstrument == p.Instrument) signedPosition += position.NativeSize;
            }
            foreach (var order in baseline.Orders.Where(x => x.State.NativeState == "open"))
            {
                var s = order.State;
                if (!instruments.ContainsKey(s.NativeInstrument) || s.AmountCurrency != "USD" || s.AmountUnit != OmsPrivateAmountUnit.Usd ||
                    s.KnownFilledNativeAmount is not { } filled || filled < 0 || filled > s.NativeAmount) return null;
                gross += s.NativeAmount - filled; if (instruments[s.NativeInstrument].BaseCurrency==target.BaseCurrency) concentration += s.NativeAmount - filled;
            }
            decimal incrementalUsd=0,deltaBound=0;var foundTarget=false;
            foreach(var bound in input.MarginBounds)
            {
                if(!instruments.TryGetValue(bound.Instrument,out var m)||m.SettlementCurrency!=bound.Currency||bound.NativeUsd<=0||bound.Buy<0||bound.Sell<0||bound.Fee<0)return null;
                var currency=input.Currencies.SingleOrDefault(x=>x.Currency==bound.Currency);if(currency is null)return null;
                equity=Math.Min(equity,collateralBounds[currency.Currency]*currency.Fx/policy.FxStressFactor);
                incrementalUsd+=(Math.Max(bound.Buy,bound.Sell)+bound.Fee)*policy.MarginMultiplier*currency.Fx*policy.FxStressFactor;
                deltaBound+=bound.NativeUsd*Math.Max(1,m.Index/m.Mark)*policy.FxStressFactor;
                if(bound.Instrument==p.Instrument)
                {foundTarget=true;if(bound.NativeUsd<p.Quantity||bound.Buy!=input.BuyMargin||bound.Sell!=input.SellMargin||bound.Fee!=input.FeeMargin)return null;}
            }
            if(!foundTarget)return null;
            gross+=Math.Max(0,deltaBound-p.Quantity);concentration+=Math.Max(0,deltaBound-p.Quantity);
            var reduce = p.Side == OrderSide.Buy ? signedPosition < 0 && p.Quantity <= -signedPosition : signedPosition > 0 && p.Quantity <= signedPosition;
            var value = new OmsRiskValuationV1(p.Kind==OmsJournalCommandKind.Replace?2:1, command.Scope, Convert.ToHexString(OmsRecoveryRunCodec.Encode(baseline).Sha256),
                Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256), input.StartedUtc,
                new[] { input.StartedUtc.AddSeconds(5), baseline.UsableThroughUtc, policy.ValidThroughUtc }.Min(),
                true, true, true, reduce, p.Quantity, deltaBound, gross, concentration, equity, margin,
                incrementalUsd, loss);
            return new(policy.Limits, value);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException) { return null; }
    }
}
