namespace TradeOps.Infrastructure.Exchange.Bitget;

public sealed class BitgetApiException : Exception
{
    public BitgetApiException(string code, string message)
        : base($"Bitget API error {code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
