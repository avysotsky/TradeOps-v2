namespace TradeOps.Domain.Entities;

public sealed class OperationalRunRecord
{
    public Guid Id { get; set; }

    public required string RunType { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsRunning { get; set; }

    public bool? Succeeded { get; set; }

    public int OrdersScanned { get; set; }

    public int OrdersUpdated { get; set; }

    public int OrderIssues { get; set; }

    public int OrdersMissingOnExchange { get; set; }

    public int PositionsCompared { get; set; }

    public int PositionMismatches { get; set; }

    public int PositionSnapshots { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
