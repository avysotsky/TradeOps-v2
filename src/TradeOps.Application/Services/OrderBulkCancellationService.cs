using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public sealed class OrderBulkCancellationService(
    IOrderCancellationCandidateRepository candidateRepository,
    IOrderCancellationService orderCancellationService,
    ILogger<OrderBulkCancellationService> logger) : IOrderBulkCancellationService
{
    public async Task<BulkOrderCancellationResult> CancelOpenOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol)
            ? null
            : symbol.Trim().ToUpperInvariant();

        var candidates = await candidateRepository.GetCancellationCandidatesAsync(
            normalizedSymbol,
            cancellationToken);

        var results = new List<OrderCancellationResult>(candidates.Count);

        foreach (var order in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await orderCancellationService.CancelAsync(
                    order.ClientOrderId,
                    cancellationToken);
                results.Add(result);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    exception,
                    "Bulk cancellation failed unexpectedly for local order {ClientOrderId}.",
                    order.ClientOrderId);

                results.Add(new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    order,
                    "Bulk cancellation encountered an unexpected error for this order."));
            }
        }

        var summary = new BulkOrderCancellationResult(normalizedSymbol, results);

        logger.LogInformation(
            "Bulk cancellation completed for scope {Symbol}: candidates={CandidateCount}, cancelled={CancelledCount}, pending={CancellationRequestedCount}, unresolved={UnresolvedCount}.",
            normalizedSymbol ?? "ALL",
            summary.CandidateCount,
            summary.CancelledCount,
            summary.CancellationRequestedCount,
            summary.UnresolvedCount);

        return summary;
    }
}
