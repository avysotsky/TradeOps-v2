namespace TradeOps.Infrastructure.Exchange.Deribit;

public sealed class DeribitApiException : Exception
{
    public DeribitApiException(int code, string message)
        : base($"Deribit API error {code}: {message}")
    {
        Code = code;
    }

    public int Code { get; }
}
