using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Enumerates a whole supported currency/kind group, not just previously known symbols.</summary>
public sealed record DeribitCurrencyRecoveryQuery(AccountKey Scope, Guid Epoch, string Currency, string Kind,
    DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, TimeSpan Overlap,
    int PageSize = 100, int MaximumPagesPerSource = 10, int MaximumFacts = 5000);

public enum DeribitCurrencyRecoveryProblem { ReadFailed = 1, UnmappedInstrument, FactCollision, Pagination, Capacity, BindingInvalid }
public sealed record DeribitCurrencyRecoveryResult(AccountKey Scope, Guid Epoch, string Currency, string Kind,
    DateTimeOffset QueryFromUtc, DateTimeOffset ThroughUtc, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    IReadOnlyList<DeribitRecoverySourceRead> Sources, IReadOnlyList<OmsPrivateEventEnvelope> Observations,
    IReadOnlyList<DeribitCurrencyRecoveryProblem> Problems, IReadOnlyList<string>? UnmappedInstruments = null)
{
    public bool TraversalComplete => Problems.Count == 0 && Sources.Count == 5 && Sources.All(x => x.Exhausted);
    public bool HistoricalUnfilledOrdersCovered => false;
    public bool OrderHistoryComplete => false;
    public bool VenueCoherent => false;
    public bool AccountWideCoverage => false;
}
public interface IDeribitCurrencyRecoveryQueries
{
    Task<DeribitCurrencyRecoveryResult> CollectAsync(DeribitCurrencyRecoveryQuery query, CancellationToken ct = default);
}
