namespace TradeOps.Infrastructure.Exchange.Gate;

public sealed class GateApiException : Exception
{
    public GateApiException(string label, string message)
        : base($"Gate API error {label}: {message}")
    {
        Label = label;
    }

    public string Label { get; }
}
