using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class OrderCancellationService(
    IExchangeClient exchangeClient,
    IOperatorReadRepository operatorReadRepository,
    IOrderRepository orderRepository,
    IOrderStateMachine orderStateMachine,
    IAlertService alertService,
    ILogger<OrderCancellationService> logger) : IOrderCancellationService
{
    public async Task<OrderCancellationResult> CancelAsync(
        string idOrClientOrderId,
        CancellationToken cancellationToken = default)
    {
        var key = idOrClientOrderId?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.NotFound,
                null,
                "Local order identifier is required.");
        }

        var order = await operatorReadRepository.GetLocalOrderAsync(key, cancellationToken);
        if (order is null)
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.NotFound,
                null,
                $"Local order '{key}' was not found.");
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.AlreadyCancelled,
                order,
                "Order is already cancelled.");
        }

        if (order.Status is OrderStatus.Filled or OrderStatus.Rejected)
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.NotCancellable,
                order,
                $"Order is already terminal ({order.Status}).");
        }

        if (order.Status == OrderStatus.Created)
        {
            orderStateMachine.Apply(
                order,
                OrderStatus.Cancelled,
                order.FilledQuantity,
                order.AverageFillPrice,
                order.ExchangeOrderId,
                order.Price);

            await orderRepository.UpdateAsync(order, cancellationToken);
            await SendCancelledAlertAsync(order, cancellationToken);

            return new OrderCancellationResult(
                OrderCancellationOutcome.Cancelled,
                order,
                "Order was cancelled locally before exchange submission.");
        }

        if (string.IsNullOrWhiteSpace(order.ExchangeOrderId))
        {
            Order? resolved;
            try
            {
                resolved = await exchangeClient.GetOrderByClientOrderIdAsync(
                    order.ClientOrderId,
                    order.Symbol,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Could not resolve exchange order id for local order {ClientOrderId} before cancellation.",
                    order.ClientOrderId);

                return new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    order,
                    "Exchange order identity could not be resolved safely.");
            }

            if (resolved is null)
            {
                return new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    order,
                    "Exchange order identity could not be resolved safely.");
            }

            await ApplyExchangeSnapshotAsync(order, resolved, cancellationToken);

            if (order.Status == OrderStatus.Cancelled)
            {
                return new OrderCancellationResult(
                    OrderCancellationOutcome.AlreadyCancelled,
                    order,
                    "Exchange already reports the order as cancelled.");
            }

            if (order.Status is OrderStatus.Filled or OrderStatus.Rejected)
            {
                return new OrderCancellationResult(
                    OrderCancellationOutcome.NotCancellable,
                    order,
                    $"Exchange already reports terminal state {order.Status}.");
            }

            if (string.IsNullOrWhiteSpace(order.ExchangeOrderId))
            {
                return new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    order,
                    "Exchange order identity is still unavailable after reconciliation.");
            }
        }

        var exchangeOrderId = order.ExchangeOrderId!;

        try
        {
            await exchangeClient.CancelOrderAsync(
                exchangeOrderId,
                order.Symbol,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Cancellation outcome is ambiguous for local order {ClientOrderId}; reconciling instead of retrying blindly.",
                order.ClientOrderId);

            var reconciled = await TryLoadExchangeOrderAsync(order, cancellationToken);
            if (reconciled is null)
            {
                return new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    order,
                    "Cancellation outcome is ambiguous and exchange state could not be reconciled.");
            }

            await ApplyExchangeSnapshotAsync(order, reconciled, cancellationToken);
            return await MapObservedOutcomeAsync(
                order,
                "Cancellation request failed, but exchange state was reconciled.",
                cancellationToken);
        }

        logger.LogInformation(
            "Cancellation request acknowledged for local order {ClientOrderId}, exchange order {ExchangeOrderId}.",
            order.ClientOrderId,
            exchangeOrderId);

        var confirmed = await TryLoadExchangeOrderAsync(order, cancellationToken);
        if (confirmed is null)
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.CancellationRequested,
                order,
                "Cancellation was acknowledged, but final exchange state is not confirmed yet.");
        }

        await ApplyExchangeSnapshotAsync(order, confirmed, cancellationToken);
        return await MapObservedOutcomeAsync(
            order,
            "Cancellation request was acknowledged by the exchange.",
            cancellationToken);
    }

    private async Task<OrderCancellationResult> MapObservedOutcomeAsync(
        Order order,
        string activeMessage,
        CancellationToken cancellationToken)
    {
        if (order.Status == OrderStatus.Cancelled)
        {
            await SendCancelledAlertAsync(order, cancellationToken);
            return new OrderCancellationResult(
                OrderCancellationOutcome.Cancelled,
                order,
                "Cancellation is confirmed by the exchange.");
        }

        if (order.Status is OrderStatus.Filled or OrderStatus.Rejected)
        {
            return new OrderCancellationResult(
                OrderCancellationOutcome.NotCancellable,
                order,
                $"Order reached terminal state {order.Status} before cancellation completed.");
        }

        return new OrderCancellationResult(
            OrderCancellationOutcome.CancellationRequested,
            order,
            activeMessage);
    }

    private async Task<Order?> TryLoadExchangeOrderAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(order.ExchangeOrderId))
            {
                var byExchangeId = await exchangeClient.GetOrderAsync(
                    order.ExchangeOrderId,
                    order.Symbol,
                    cancellationToken);

                if (byExchangeId is not null)
                {
                    return byExchangeId;
                }
            }

            return await exchangeClient.GetOrderByClientOrderIdAsync(
                order.ClientOrderId,
                order.Symbol,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Exchange reconciliation lookup failed for local order {ClientOrderId}.",
                order.ClientOrderId);
            return null;
        }
    }

    private async Task ApplyExchangeSnapshotAsync(
        Order localOrder,
        Order exchangeOrder,
        CancellationToken cancellationToken)
    {
        if (!orderStateMachine.CanTransition(localOrder.Status, exchangeOrder.Status))
        {
            logger.LogWarning(
                "Ignoring stale or invalid exchange state regression {LocalStatus} -> {ExchangeStatus} for {ClientOrderId}.",
                localOrder.Status,
                exchangeOrder.Status,
                localOrder.ClientOrderId);
            return;
        }

        var useExchangeFill = exchangeOrder.FilledQuantity >= localOrder.FilledQuantity;
        var filledQuantity = Math.Max(localOrder.FilledQuantity, exchangeOrder.FilledQuantity);
        var averageFillPrice = useExchangeFill
            ? exchangeOrder.AverageFillPrice
            : localOrder.AverageFillPrice;

        orderStateMachine.Apply(
            localOrder,
            exchangeOrder.Status,
            filledQuantity,
            averageFillPrice,
            exchangeOrder.ExchangeOrderId,
            exchangeOrder.Price ?? localOrder.Price);

        await orderRepository.UpdateAsync(localOrder, cancellationToken);
    }

    private Task SendCancelledAlertAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Order {ClientOrderId} cancellation confirmed with status {Status}.",
            order.ClientOrderId,
            order.Status);

        return alertService.SendAsync(
            new AlertMessage(
                "OrderCancelled",
                $"{order.ClientOrderId}: cancellation confirmed; filled {order.FilledQuantity}/{order.RequestedQuantity} {order.Symbol}.",
                AlertSeverity.Info,
                order.ClientOrderId),
            cancellationToken);
    }
}
