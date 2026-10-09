namespace TradeOps.Infrastructure.Exchange.Binance;

public sealed class BinanceApiException : Exception
{
    public BinanceApiException(int code, string message)
        : base($"Binance API error {code}: {message}")
    {
        Code = code;
    }

    public int Code { get; }
}
