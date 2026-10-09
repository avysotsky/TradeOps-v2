namespace TradeOps.Infrastructure.Exchange.Bybit;

public sealed class BybitApiException : Exception
{
    public BybitApiException(int returnCode, string message)
        : base($"Bybit API error {returnCode}: {message}")
    {
        ReturnCode = returnCode;
    }

    public int ReturnCode { get; }
}
