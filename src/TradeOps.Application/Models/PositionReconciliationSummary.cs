using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record PositionReconciliationIssue(
    string Symbol,
    OrderSide? LocalSide,
    decimal LocalQuantity,
    OrderSide? ExchangeSide,
    decimal ExchangeQuantity,
    string Reason);

public sealed record PositionReconciliationSummary(
    int SymbolsCompared,
    int Matched,
    int Mismatched,
    IReadOnlyCollection<PositionReconciliationIssue> Issues);
