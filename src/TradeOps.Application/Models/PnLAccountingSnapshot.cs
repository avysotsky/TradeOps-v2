namespace TradeOps.Application.Models;

public sealed record UnconvertedFee(
    string ExchangeFillId,
    decimal Amount,
    string? Currency,
    DateTimeOffset FilledAt);

public sealed record PnLAccountingSnapshot(
    string SettlementCurrency,
    decimal GrossRealizedPnL,
    decimal SettlementFees,
    decimal? NetRealizedPnL,
    IReadOnlyCollection<UnconvertedFee> UnconvertedFees,
    DateTimeOffset CalculatedAt)
{
    public bool IsComplete => NetRealizedPnL.HasValue;
}
