using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Contracts;

internal static class OperatorResponseMapper
{
    public static LocalOrderResponse ToLocalOrder(Order order) =>
        new(
            order.Id,
            order.ExchangeOrderId,
            order.ClientOrderId,
            order.Symbol,
            order.Side,
            order.OrderType,
            order.RequestedQuantity,
            order.FilledQuantity,
            order.AverageFillPrice,
            order.Price,
            order.Status,
            order.CreatedAt,
            order.UpdatedAt);

    public static OrderCancellationResponse ToOrderCancellation(
        OrderCancellationResult result) =>
        new(
            result.Outcome,
            result.Order is null ? null : ToLocalOrder(result.Order),
            result.Message);

    public static BulkOrderCancellationResponse ToBulkOrderCancellation(
        BulkOrderCancellationResult result) =>
        new(
            result.Symbol,
            result.CandidateCount,
            result.CancelledCount,
            result.AlreadyCancelledCount,
            result.CancellationRequestedCount,
            result.NotCancellableCount,
            result.NotFoundCount,
            result.UnresolvedCount,
            result.IsComplete,
            result.Results.Select(ToOrderCancellation).ToArray());
}
