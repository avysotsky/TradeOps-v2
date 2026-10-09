using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record ReconciliationSummary(
    int Scanned,
    int Updated,
    int Unchanged,
    int MissingOnExchange,
    IReadOnlyCollection<ReconciliationIssue> Issues);

public sealed record ReconciliationIssue(
    string ClientOrderId,
    OrderStatus LocalStatus,
    OrderStatus? ExchangeStatus,
    string Message);
