namespace TradeOps.Api.Contracts;

public sealed record TradingViewWebhookRequest(
    string EventId,
    string Symbol,
    string Action,
    decimal Quantity,
    decimal? RiskPercent = null,
    decimal? StopLoss = null,
    decimal? TakeProfit = null);
