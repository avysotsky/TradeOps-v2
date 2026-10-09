using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class SignalExecutionService(
    ITradingSignalRepository tradingSignalRepository,
    IOrderRepository orderRepository,
    IOrderManager orderManager,
    IClientOrderIdGenerator clientOrderIdGenerator,
    ILogger<SignalExecutionService> logger) : ISignalExecutionService
{
    public async Task<SignalExecutionResult> ExecuteSignalAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default)
    {
        var existingSignal = await tradingSignalRepository.GetByIdAsync(
            signal.Id,
            cancellationToken);

        if (existingSignal is not null)
        {
            EnsureExecutionIdentityMatches(existingSignal, signal);

            return await ReuseExistingSignalAsync(
                existingSignal,
                cancellationToken);
        }

        var inserted = await tradingSignalRepository.TryAddAsync(
            signal,
            cancellationToken);

        if (!inserted)
        {
            existingSignal = await tradingSignalRepository.GetByIdAsync(
                signal.Id,
                cancellationToken);

            if (existingSignal is null)
            {
                throw new InvalidOperationException(
                    $"Duplicate SignalId '{signal.Id}' was detected, but the existing signal audit could not be loaded.");
            }

            EnsureExecutionIdentityMatches(existingSignal, signal);

            logger.LogInformation(
                "Concurrent duplicate signal {SignalId} resolved to the persisted logical signal.",
                signal.Id);

            return await ReuseExistingSignalAsync(
                existingSignal,
                cancellationToken);
        }

        return await ExecuteAndFinalizeAuditAsync(
            signal,
            cancellationToken);
    }

    private static void EnsureExecutionIdentityMatches(
        TradingSignal persisted,
        TradingSignal incoming)
    {
        var conflictingFields = new List<string>();

        if (!string.Equals(persisted.Symbol, incoming.Symbol, StringComparison.Ordinal))
        {
            conflictingFields.Add(nameof(TradingSignal.Symbol));
        }

        if (persisted.Side != incoming.Side)
        {
            conflictingFields.Add(nameof(TradingSignal.Side));
        }

        if (!string.Equals(persisted.SignalType, incoming.SignalType, StringComparison.Ordinal))
        {
            conflictingFields.Add(nameof(TradingSignal.SignalType));
        }

        if (persisted.RequestedQuantity != incoming.RequestedQuantity)
        {
            conflictingFields.Add(nameof(TradingSignal.RequestedQuantity));
        }

        if (persisted.RiskPercent != incoming.RiskPercent)
        {
            conflictingFields.Add(nameof(TradingSignal.RiskPercent));
        }

        if (persisted.StopLoss != incoming.StopLoss)
        {
            conflictingFields.Add(nameof(TradingSignal.StopLoss));
        }

        if (persisted.TakeProfit != incoming.TakeProfit)
        {
            conflictingFields.Add(nameof(TradingSignal.TakeProfit));
        }

        if (conflictingFields.Count > 0)
        {
            throw new SignalIdConflictException(incoming.Id, conflictingFields);
        }
    }

    private async Task<SignalExecutionResult> ReuseExistingSignalAsync(
        TradingSignal signal,
        CancellationToken cancellationToken)
    {
        if (signal.Outcome == SignalOutcome.Rejected)
        {
            logger.LogInformation(
                "Idempotent signal retry {SignalId} returned persisted risk rejection.",
                signal.Id);

            return new SignalExecutionResult(
                signal.Id,
                false,
                signal.RiskRejectionReasons,
                null);
        }

        if (signal.Outcome == SignalOutcome.Accepted)
        {
            var clientOrderId = signal.ClientOrderId
                ?? clientOrderIdGenerator.Generate(signal.Id);

            var order = await orderRepository.GetByClientOrderIdAsync(
                clientOrderId,
                cancellationToken);

            if (order is null)
            {
                throw new InvalidOperationException(
                    $"Signal '{signal.Id}' is marked Accepted, but linked order '{clientOrderId}' could not be loaded.");
            }

            try
            {
                OrderExecutionIdentityGuard.EnsureMatches(order, signal);
            }
            catch (ClientOrderIdConflictException exception)
            {
                await PersistExecutionIssueAsync(signal, exception, cancellationToken);
                throw;
            }

            if (HasExecutionIssue(signal))
            {
                ClearExecutionIssue(signal);
                await tradingSignalRepository.UpdateAsync(signal, cancellationToken);
            }

            logger.LogInformation(
                "Idempotent signal retry {SignalId} returned linked order {ClientOrderId} with status {Status}.",
                signal.Id,
                clientOrderId,
                order.Status);

            return FromExistingOrder(signal.Id, order);
        }

        logger.LogInformation(
            "Signal {SignalId} has a persisted Received audit without a terminal outcome; resuming idempotent execution.",
            signal.Id);

        return await ExecuteAndFinalizeAuditAsync(
            signal,
            cancellationToken);
    }

    private async Task<SignalExecutionResult> ExecuteAndFinalizeAuditAsync(
        TradingSignal signal,
        CancellationToken cancellationToken)
    {
        SignalExecutionResult result;
        try
        {
            result = await orderManager.ExecuteSignalAsync(
                signal,
                cancellationToken);
        }
        catch (ClientOrderIdConflictException exception)
        {
            await PersistExecutionIssueAsync(signal, exception, cancellationToken);
            throw;
        }

        ClearExecutionIssue(signal);

        if (!result.Accepted)
        {
            signal.Outcome = SignalOutcome.Rejected;
            signal.RiskRejectionReasons = result.RiskReasons.ToArray();
            signal.OrderId = null;
            signal.ClientOrderId = null;

            await tradingSignalRepository.UpdateAsync(
                signal,
                cancellationToken);

            return result;
        }

        if (result.Order is null)
        {
            throw new InvalidOperationException(
                $"Accepted signal '{signal.Id}' did not return an order result.");
        }

        var localOrder = await orderRepository.GetByClientOrderIdAsync(
            result.Order.ClientOrderId,
            cancellationToken);

        if (localOrder is null)
        {
            throw new InvalidOperationException(
                $"Accepted signal '{signal.Id}' returned order '{result.Order.ClientOrderId}', but the local order could not be loaded.");
        }

        signal.Outcome = SignalOutcome.Accepted;
        signal.RiskRejectionReasons = [];
        signal.OrderId = localOrder.Id;
        signal.ClientOrderId = localOrder.ClientOrderId;

        await tradingSignalRepository.UpdateAsync(
            signal,
            cancellationToken);

        return result;
    }

    private async Task PersistExecutionIssueAsync(
        TradingSignal signal,
        ClientOrderIdConflictException exception,
        CancellationToken cancellationToken)
    {
        signal.ExecutionIssueCode = "ClientOrderIdConflict";
        signal.ExecutionIssueMessage = exception.Message;
        signal.ExecutionIssueAt = DateTimeOffset.UtcNow;

        await tradingSignalRepository.UpdateAsync(signal, cancellationToken);
    }

    private static bool HasExecutionIssue(TradingSignal signal) =>
        signal.ExecutionIssueCode is not null
        || signal.ExecutionIssueMessage is not null
        || signal.ExecutionIssueAt.HasValue;

    private static void ClearExecutionIssue(TradingSignal signal)
    {
        signal.ExecutionIssueCode = null;
        signal.ExecutionIssueMessage = null;
        signal.ExecutionIssueAt = null;
    }

    private static SignalExecutionResult FromExistingOrder(
        Guid signalId,
        Order order)
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
