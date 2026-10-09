namespace TradeOps.Infrastructure.Exchange.KuCoin;

public sealed class KuCoinApiException : Exception
{
    public KuCoinApiException(string code, string message)
        : base($"KuCoin API error {code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
