namespace TradeOps.Infrastructure.Exchange;

public static class ExchangeProviders
{
    public const string Mock = "Mock";
    public const string BybitTestnet = "BybitTestnet";
    public const string BinanceFuturesTestnet = "BinanceFuturesTestnet";
    public const string HyperliquidTestnet = "HyperliquidTestnet";
    public const string MexcFuturesReadOnly = "MexcFuturesReadOnly";
    public const string DeribitTestnet = "DeribitTestnet";
    public const string OkxDemo = "OkxDemo";
    public const string BitgetDemo = "BitgetDemo";
    public const string GateFuturesTestnet = "GateFuturesTestnet";
    public const string KrakenFuturesReadOnly = "KrakenFuturesReadOnly";
    public const string KuCoinFuturesReadOnly = "KuCoinFuturesReadOnly";
    public const string CoinbaseIntxSandbox = "CoinbaseIntxSandbox";
    public const string IbkrPaper = "IbkrPaper";
}

public sealed class ExchangeOptions
{
    public const string SectionName = "Exchange";

    public string Provider { get; init; } = ExchangeProviders.Mock;
}
