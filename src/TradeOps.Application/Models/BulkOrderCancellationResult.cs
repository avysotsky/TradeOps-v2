namespace TradeOps.Application.Models;

public sealed record BulkOrderCancellationResult(
    string? Symbol,
    IReadOnlyCollection<OrderCancellationResult> Results)
{
    public int CandidateCount => Results.Count;

    public int CancelledCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.Cancelled);

    public int AlreadyCancelledCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.AlreadyCancelled);

    public int CancellationRequestedCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.CancellationRequested);

    public int NotCancellableCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.NotCancellable);

    public int NotFoundCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.NotFound);

    public int UnresolvedCount => Results.Count(result =>
        result.Outcome == OrderCancellationOutcome.Unresolved);

    public bool IsComplete =>
        CancellationRequestedCount == 0
        && NotFoundCount == 0
        && UnresolvedCount == 0;
}
