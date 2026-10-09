namespace TradeOps.Application.Models;

public sealed record SignalAuditCursorPosition(
    DateTimeOffset CreatedAt,
    Guid Id);
