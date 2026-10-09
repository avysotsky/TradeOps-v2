using TradeOps.Domain.Entities;

namespace TradeOps.Application.Models;

public sealed record SignalAuditPageResult(
    IReadOnlyCollection<TradingSignal> Signals,
    bool HasMore);
