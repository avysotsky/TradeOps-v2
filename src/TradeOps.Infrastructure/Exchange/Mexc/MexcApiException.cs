namespace TradeOps.Infrastructure.Exchange.Mexc;

public sealed class MexcApiException : Exception
{
    public MexcApiException(int code, string message)
        : base($"MEXC Contract API error {code}: {message}")
    {
        Code = code;
    }

    public int Code { get; }
}
