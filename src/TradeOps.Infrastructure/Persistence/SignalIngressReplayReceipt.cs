namespace TradeOps.Infrastructure.Persistence;

public sealed class SignalIngressReplayReceipt
{
    public Guid RequestId { get; set; }

    public DateTimeOffset RequestTimestamp { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
