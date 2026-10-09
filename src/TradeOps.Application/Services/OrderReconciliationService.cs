using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class OrderReconciliationService(
    IExchangeClient exchangeClient,
    IOrderRepository orderRepository,
    IOrderStateMachine orderStateMachine,
    IAlertService alertService,
    IOperationalRunStatusRepository runStatusRepository,
    ILogger<OrderReconciliationService> logger) : IOrderReconciliationService
{
    public async Task<ReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await runStatusRepository.MarkStartedAsync(
            OperationalRunTypes.OrderReconciliation,
            cancellationToken);

        try
        {
            var localOrders = await orderRepository.GetReconciliationCandidatesAsync(cancellationToken);
            var issues = new List<ReconciliationIssue>();
            var updated = 0;
            var unchanged = 0;
            var missing = 0;

            foreach (var localOrder in localOrders)
            {
                var exchangeOrder = !string.IsNullOrWhiteSpace(localOrder.ExchangeOrderId)
                    ? await exchangeClient.GetOrderAsync(
                        localOrder.ExchangeOrderId,
                        localOrder.Symbol,
                        cancellationToken)
                    : await exchangeClient.GetOrderByClientOrderIdAsync(
                        localOrder.ClientOrderId,
                        localOrder.Symbol,
                        cancellationToken);

                if (exchangeOrder is null)
                {
                    missing++;
                    var previousStatus = localOrder.Status;

                    if (localOrder.Status != OrderStatus.Unknown)
                    {
                        orderStateMachine.Apply(
                            localOrder,
                            OrderStatus.Unknown,
                            localOrder.FilledQuantity,
                            localOrder.AverageFillPrice,
                            localOrder.ExchangeOrderId,
                            localOrder.Price);

                        await orderRepository.UpdateAsync(localOrder, cancellationToken);
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }

                    issues.Add(new ReconciliationIssue(
                        localOrder.ClientOrderId,
                        previousStatus,
                        null,
                        "Exchange did not return the order. Local state remains unresolved."));

                    continue;
                }

                if (!HasSameIdentity(localOrder, exchangeOrder))
                {
                    issues.Add(new ReconciliationIssue(
                        localOrder.ClientOrderId,
                        localOrder.Status,
                        exchangeOrder.Status,
                        "Exchange order identity does not match local symbol, side, or requested quantity."));
                    continue;
                }

                if (HasSameState(localOrder, exchangeOrder))
                {
                    unchanged++;
                    continue;
                }

                try
                {
                    var previousStatus = localOrder.Status;

                    orderStateMachine.Apply(
                        localOrder,
                        exchangeOrder.Status,
                        exchangeOrder.FilledQuantity,
                        exchangeOrder.AverageFillPrice,
                        exchangeOrder.ExchangeOrderId,
                        exchangeOrder.Price);

                    await orderRepository.UpdateAsync(localOrder, cancellationToken);
                    updated++;

                    logger.LogInformation(
                        "Reconciled order {ClientOrderId}: {PreviousStatus} -> {CurrentStatus}, filled {FilledQuantity}/{RequestedQuantity}.",
                        localOrder.ClientOrderId,
                        previousStatus,
                        localOrder.Status,
                        localOrder.FilledQuantity,
                        localOrder.RequestedQuantity);
                }
                catch (InvalidOperationException exception)
                {
                    issues.Add(new ReconciliationIssue(
                        localOrder.ClientOrderId,
                        localOrder.Status,
                        exchangeOrder.Status,
                        exception.Message));
                }
            }

            var summary = new ReconciliationSummary(
                localOrders.Count,
                updated,
                unchanged,
                missing,
                issues);

            await runStatusRepository.MarkCompletedAsync(
                OperationalRunTypes.OrderReconciliation,
                new OperationalRunMetrics(
                    OrdersScanned: summary.Scanned,
                    OrdersUpdated: summary.Updated,
                    OrderIssues: summary.Issues.Count,
                    OrdersMissingOnExchange: summary.MissingOnExchange),
                cancellationToken);

            logger.LogInformation(
                "Reconciliation completed. Scanned={Scanned}, Updated={Updated}, Unchanged={Unchanged}, MissingOnExchange={MissingOnExchange}, Issues={IssueCount}.",
                summary.Scanned,
                summary.Updated,
                summary.Unchanged,
                summary.MissingOnExchange,
                summary.Issues.Count);

            if (summary.Issues.Count > 0)
            {
                await alertService.SendAsync(
                    new AlertMessage(
                        "ReconciliationMismatch",
                        $"Reconciliation found {summary.Issues.Count} issue(s); scanned={summary.Scanned}, updated={summary.Updated}, missing={summary.MissingOnExchange}.",
                        AlertSeverity.Warning),
                    cancellationToken);
            }

            return summary;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            await runStatusRepository.MarkFailedAsync(
                OperationalRunTypes.OrderReconciliation,
                exception.Message,
                cancellationToken);
            throw;
        }
    }

    private static bool HasSameIdentity(Order localOrder, Order exchangeOrder)
    {
        return string.Equals(localOrder.Symbol, exchangeOrder.Symbol, StringComparison.OrdinalIgnoreCase)
            && localOrder.Side == exchangeOrder.Side
            && localOrder.RequestedQuantity == exchangeOrder.RequestedQuantity;
    }

    private static bool HasSameState(Order localOrder, Order exchangeOrder)
    {
        return localOrder.Status == exchangeOrder.Status
            && localOrder.FilledQuantity == exchangeOrder.FilledQuantity
            && localOrder.AverageFillPrice == exchangeOrder.AverageFillPrice
            && localOrder.ExchangeOrderId == exchangeOrder.ExchangeOrderId;
    }
}
