namespace TradeOps.Domain.Entities;

public sealed class AccountInfo
{
    public required string Currency { get; init; }

    public decimal Balance { get; init; }

    public decimal Equity { get; init; }

    public decimal AvailableBalance { get; init; }
}
