namespace TradeOps.Infrastructure.Exchange.Okx;

public sealed class OkxApiException : Exception
{
    public OkxApiException(string code, string message)
        : base($"OKX API error {code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
