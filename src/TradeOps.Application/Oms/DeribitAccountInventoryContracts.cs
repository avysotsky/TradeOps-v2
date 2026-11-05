using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitAccountInventoryRequest(AccountKey Scope, Guid Epoch,
    TimeSpan MaximumObservationSpan, int MaximumPositions = 4096, int MaximumOpenOrders = 4096, int MaximumCurrencies = 32);
/// <summary>Native discovery reference only. Size is not an OMS quantity until trusted instrument metadata supplies units.</summary>
public sealed record DeribitNativePositionReference(string NativeInstrument, string Kind, string Direction, decimal NativeSize);
public sealed record DeribitNativeOpenOrderReference(string NativeInstrument, string ExchangeOrderId,
    string NativeState, string NativeOrderType);
public enum DeribitAccountInventoryProblem { ReadFailed = 1, AccountMismatch, Capacity, StaleObservation, InvalidObservation }
public sealed record DeribitAccountInventoryResult(AccountKey Scope, Guid Epoch,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, TimeSpan MaximumObservationSpan,
    IReadOnlyList<DeribitCurrencyBalance> Balances, IReadOnlyList<DeribitNativePositionReference> Positions,
    IReadOnlyList<DeribitNativeOpenOrderReference> OpenOrders, IReadOnlyList<DeribitAccountInventoryProblem> Problems)
{
    public bool QueriesCompleted => Problems.Count == 0;
    public bool IsFreshAt(DateTimeOffset now) => QueriesCompleted && now >= CompletedAtUtc && now >= StartedAtUtc &&
        now - StartedAtUtc <= MaximumObservationSpan;
    public bool VenueCoherent => false;
    public bool AccountReady => false;
}
public interface IDeribitAccountInventoryReader
{
    Task<DeribitAccountInventoryResult> ReadAsync(DeribitAccountInventoryRequest request, CancellationToken ct = default);
}
