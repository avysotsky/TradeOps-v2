using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class OrderManager(
    IRiskEngine riskEngine,
    IExchangeClient exchangeClient,
    IOrderRepository orderRepository,
    IClientOrderIdGenerator clientOrderIdGenerator,
    IOrderStateMachine orderStateMachine,
    IAlertService alertService,
    ILogger<OrderManager> logger) : IOrderManager
{
    public async Task<SignalExecutionResult> ExecuteSignalAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default)
    {
        var clientOrderId = clientOrderIdGenerator.Generate(signal.Id);

        var existingOrder = await orderRepository.GetByClientOrderIdAsync(
            clientOrderId,
            cancellationToken);

        if (existingOrder is not null)
        {
            OrderExecutionIdentityGuard.EnsureMatches(existingOrder, signal);

            logger.LogInformation(
                "Idempotent signal retry returned existing order {ClientOrderId} with status {Status}.",
                clientOrderId,
                existingOrder.Status);

            return FromExistingOrder(signal.Id, existingOrder);
        }

        var riskDecision = await riskEngine.CheckAsync(signal, cancellationToken);

        if (!riskDecision.IsAllowed)
        {
            logger.LogWarning(
                "Signal {SignalId} was rejected by risk controls. Reasons: {Reasons}",
                signal.Id,
                string.Join("; ", riskDecision.Reasons));

            await alertService.SendAsync(
                new AlertMessage(
                    "RiskRejected",
                    $"Signal {signal.Id} for {signal.Symbol} was rejected: {string.Join("; ", riskDecision.Reasons)}",
                    AlertSeverity.Warning,
                    clientOrderId),
                cancellationToken);

            return new SignalExecutionResult(
                signal.Id,
                false,
                riskDecision.Reasons,
                null);
        }

        var now = DateTimeOffset.UtcNow;
        var localOrder = new Order
        {
            Id = Guid.NewGuid(),
            ClientOrderId = clientOrderId,
            Symbol = signal.Symbol,
            Side = signal.Side,
            OrderType = OrderType.Market,
            RequestedQuantity = signal.RequestedQuantity,
            FilledQuantity = 0m,
            Status = OrderStatus.Created,
            CreatedAt = now,
            UpdatedAt = now
        };

        var inserted = await orderRepository.TryAddAsync(
            localOrder,
            cancellationToken);

        if (!inserted)
        {
            existingOrder = await orderRepository.GetByClientOrderIdAsync(
                clientOrderId,
                cancellationToken);

            if (existingOrder is null)
            {
                throw new InvalidOperationException(
                    $"Duplicate ClientOrderId '{clientOrderId}' was detected, but the existing order could not be loaded.");
            }

            OrderExecutionIdentityGuard.EnsureMatches(existingOrder, signal);

            logger.LogInformation(
                "Concurrent duplicate signal resolved to existing order {ClientOrderId} with status {Status}.",
                clientOrderId,
                existingOrder.Status);

            return FromExistingOrder(signal.Id, existingOrder);
        }

        orderStateMachine.Apply(
            localOrder,
            OrderStatus.Submitted,
            0m,
            null);

        await orderRepository.UpdateAsync(localOrder, cancellationToken);

        logger.LogInformation(
            "Order {ClientOrderId} submitted for {Side} {Quantity} {Symbol}.",
            localOrder.ClientOrderId,
            localOrder.Side,
            localOrder.RequestedQuantity,
            localOrder.Symbol);

        await alertService.SendAsync(
            new AlertMessage(
                "OrderSubmitted",
                $"{localOrder.ClientOrderId}: {localOrder.Side} {localOrder.RequestedQuantity} {localOrder.Symbol} submitted.",
                AlertSeverity.Info,
                localOrder.ClientOrderId),
            cancellationToken);

        var orderRequest = new PlaceOrderRequest(
            clientOrderId,
            signal.Symbol,
            signal.Side,
            OrderType.Market,
            signal.RequestedQuantity);

        try
        {
            var exchangeResult = await exchangeClient.PlaceOrderAsync(
                orderRequest,
                cancellationToken);

            orderStateMachine.Apply(
                localOrder,
                exchangeResult.Status,
                exchangeResult.FilledQuantity,
                exchangeResult.AverageFillPrice,
                exchangeResult.ExchangeOrderId);

            await orderRepository.UpdateAsync(localOrder, cancellationToken);
            await LogAndAlertOrderStateAsync(localOrder, cancellationToken);

            return FromExistingOrder(signal.Id, localOrder);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(
                exception,
                "Timeout while submitting order {ClientOrderId}; reconciling by client order id instead of retrying blindly.",
                clientOrderId);

            var exchangeOrder = await exchangeClient.GetOrderByClientOrderIdAsync(
                clientOrderId,
                signal.Symbol,
                cancellationToken);

            if (exchangeOrder is not null)
            {
                orderStateMachine.Apply(
                    localOrder,
                    exchangeOrder.Status,
                    exchangeOrder.FilledQuantity,
                    exchangeOrder.AverageFillPrice,
                    exchangeOrder.ExchangeOrderId,
                    exchangeOrder.Price);

                await orderRepository.UpdateAsync(localOrder, cancellationToken);
                await LogAndAlertOrderStateAsync(localOrder, cancellationToken);
                return FromExistingOrder(signal.Id, localOrder);
            }

            orderStateMachine.Apply(
                localOrder,
                OrderStatus.Unknown,
                localOrder.FilledQuantity,
                localOrder.AverageFillPrice,
                localOrder.ExchangeOrderId,
                localOrder.Price);

            await orderRepository.UpdateAsync(localOrder, cancellationToken);
            await LogAndAlertOrderStateAsync(localOrder, cancellationToken);
            return FromExistingOrder(signal.Id, localOrder);
        }
    }

    private async Task LogAndAlertOrderStateAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Order {ClientOrderId} state changed to {Status}. Filled {FilledQuantity}/{RequestedQuantity} at {AverageFillPrice}.",
            order.ClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.RequestedQuantity,
            order.AverageFillPrice);

        var (eventType, severity) = order.Status switch
        {
            OrderStatus.PartiallyFilled => ("OrderPartiallyFilled", AlertSeverity.Info),
            OrderStatus.Filled => ("OrderFilled", AlertSeverity.Info),
            OrderStatus.Rejected => ("OrderRejected", AlertSeverity.Warning),
            OrderStatus.Cancelled => ("OrderCancelled", AlertSeverity.Info),
            OrderStatus.Unknown => ("OrderStateUnknown", AlertSeverity.Error),
            _ => ("OrderStateChanged", AlertSeverity.Info)
        };

        await alertService.SendAsync(
            new AlertMessage(
                eventType,
                $"{order.ClientOrderId}: {order.Status}; filled {order.FilledQuantity}/{order.RequestedQuantity} {order.Symbol}.",
                severity,
                order.ClientOrderId),
            cancellationToken);
    }

    private static SignalExecutionResult FromExistingOrder(Guid signalId, Order order)
    {
        var result = new OrderResult(
            order.ExchangeOrderId,
            order.ClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.AverageFillPrice);

        return new SignalExecutionResult(
            signalId,
            true,
            Array.Empty<string>(),
            result);
    }
}
