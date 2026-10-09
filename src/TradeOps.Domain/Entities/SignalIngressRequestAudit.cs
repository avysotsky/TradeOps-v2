using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class SignalIngressRequestAudit
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public DateTimeOffset RequestTimestamp { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public required string Method { get; set; }

    public required string Path { get; set; }

    public SignalIngressRequestOutcome Outcome { get; set; } =
        SignalIngressRequestOutcome.Received;

    public int? HttpStatusCode { get; set; }

    public Guid? SignalId { get; set; }

    public Guid? OrderId { get; set; }

    public string? ClientOrderId { get; set; }
}
