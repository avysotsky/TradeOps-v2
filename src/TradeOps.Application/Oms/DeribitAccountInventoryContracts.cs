using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitAccountInventoryRequest(AccountKey Scope, Guid Epoch,
    TimeSpan MaximumObservationSpan, int MaximumPositions = 4096, int MaximumOpenOrders = 4096, int MaximumCurrencies = 32);
/// <summary>Native discovery reference only. Size is not an OMS quantity until trusted instrument metadata supplies units.</summary>
public sealed record DeribitNativePositionReference(string NativeInstrument, string Kind, string Direction, decimal NativeSize,
    decimal? NativeBaseSize = null);
public sealed record DeribitNativeOpenOrderReference(string NativeInstrument, string ExchangeOrderId,
    string NativeState, string NativeOrderType, string? NativeFingerprint = null);
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

public static class DeribitAccountInventoryComparison
{
    // Mark-derived equity/base-size can move without a position or cash mutation.
    public static bool SameEconomicState(DeribitAccountInventoryResult a, DeribitAccountInventoryResult b) =>
        a.Scope == b.Scope && a.Epoch == b.Epoch &&
        a.Balances.Select(x => (x.Currency, x.CashBalance, x.CrossCollateralEnabled, x.PortfolioMarginingEnabled)).OrderBy(x => x.Currency, StringComparer.Ordinal)
            .SequenceEqual(b.Balances.Select(x => (x.Currency, x.CashBalance, x.CrossCollateralEnabled, x.PortfolioMarginingEnabled)).OrderBy(x => x.Currency, StringComparer.Ordinal)) &&
        a.Positions.Select(x => (x.NativeInstrument, x.Kind, x.Direction, x.NativeSize)).OrderBy(x => x.NativeInstrument, StringComparer.Ordinal)
            .SequenceEqual(b.Positions.Select(x => (x.NativeInstrument, x.Kind, x.Direction, x.NativeSize)).OrderBy(x => x.NativeInstrument, StringComparer.Ordinal)) &&
        a.OpenOrders.OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ThenBy(x => x.ExchangeOrderId, StringComparer.Ordinal)
            .SequenceEqual(b.OpenOrders.OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ThenBy(x => x.ExchangeOrderId, StringComparer.Ordinal));
}
