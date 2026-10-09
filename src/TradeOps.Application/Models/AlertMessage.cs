namespace TradeOps.Application.Models;

public sealed record AlertMessage(
    string EventType,
    string Message,
    AlertSeverity Severity = AlertSeverity.Info,
    string? CorrelationId = null);
