namespace TradeOps.Application.Oms;

public enum DeribitHistoryCoverageMode { InventoryCurrenciesV1=1, ListedDerivativeSettlementsV2=2 }

/// <summary>Explicitly separates collateral inventory from derivative order/trade history.
/// This does not assert cash-transaction history for collateral-only currencies.</summary>
public sealed record DeribitCollateralHistoryCoverageV2(int SchemaVersion,string Policy,
    IReadOnlyList<string> CollateralCurrencies,IReadOnlyList<string> DerivativeSettlementCurrencies,
    IReadOnlyList<string> UnqueriedCollateralCashCurrencies)
{
    public const string PolicyName="deribit-listed-derivatives-settlements-v2";
    public static IReadOnlyList<string> Settlements {get;}=Array.AsReadOnly<string>(["BTC","ETH","EURR","USDC","USDT"]);
    private static readonly HashSet<string> KnownCollateral=new(["BNB","BTC","BUIDL","ETH","ETHW","EURR","MATIC","PAXG","SOL","STETH","USDC","USDE","USDT","USYC","XRP"],StringComparer.Ordinal);
    internal static bool Known(string currency)=>KnownCollateral.Contains(currency);
    internal static DeribitCollateralHistoryCoverageV2 Create(IReadOnlyList<DeribitCurrencyBalance> balances)
    {
        var currencies=balances.Select(x=>x.Currency).Order(StringComparer.Ordinal).ToArray();
        return new(2,PolicyName,currencies,Settlements,currencies.Except(Settlements,StringComparer.Ordinal).ToArray());
    }
    public static bool IsValid(DeribitCollateralHistoryCoverageV2 value,IReadOnlyList<DeribitCurrencyBalance> balances)=>
        value.SchemaVersion==2&&value.Policy==PolicyName&&value.CollateralCurrencies is{Count:>0 and <=32}&&value.DerivativeSettlementCurrencies is not null&&value.UnqueriedCollateralCashCurrencies is not null&&
        value.CollateralCurrencies.All(Known)&&value.CollateralCurrencies.SequenceEqual(balances.Select(x=>x.Currency).Order(StringComparer.Ordinal))&&
        value.DerivativeSettlementCurrencies.SequenceEqual(Settlements)&&
        value.UnqueriedCollateralCashCurrencies.SequenceEqual(value.CollateralCurrencies.Except(Settlements,StringComparer.Ordinal));
}
