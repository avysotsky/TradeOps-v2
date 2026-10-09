using TradeOps.Domain.Entities;

namespace TradeOps.Application.Models;

public enum OrderCancellationOutcome
{
    Cancelled,
    AlreadyCancelled,
    CancellationRequested,
    NotCancellable,
    Unresolved,
    NotFound
}

public sealed record OrderCancellationResult(
    OrderCancellationOutcome Outcome,
    Order? Order,
    string? Message = null);
