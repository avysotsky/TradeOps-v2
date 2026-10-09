using TradeOps.Application.Models;

namespace TradeOps.Api.Contracts;

public sealed record TradingViewWebhookResponse(
    string EventId,
    Guid SignalId,
    bool Accepted,
    IReadOnlyCollection<string> RiskReasons,
    OrderResult? Order);
