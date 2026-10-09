namespace TradeOps.Infrastructure.Exchange.Coinbase;

public sealed class CoinbaseIntxApiException : Exception
{
    public CoinbaseIntxApiException(int statusCode, string message)
        : base($"Coinbase INTX API error {statusCode}: {message}")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
