using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

// Instrument-scoped query, deliberately not an account-wide coverage/readiness contract.
public sealed record DeribitRecoveryQuery(AccountKey Scope, Guid Epoch, string NativeInstrument,
    DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, TimeSpan Overlap,
    int PageSize = 100, int MaximumPagesPerSource = 10, int MaximumFacts = 5000);

public enum DeribitRecoveryQueryProblem
{
    Authentication, AccountMismatch, Protocol, Transport, Deadline, Capacity, Pagination, FactCollision
}

public sealed record DeribitRecoverySourceRead(string Source, int Pages, bool Exhausted);

public sealed record DeribitRecoveryQueryResult(AccountKey Scope, Guid Epoch, string NativeInstrument,
    string Currency, DateTimeOffset QueryFromUtc, DateTimeOffset ThroughUtc,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    IReadOnlyList<DeribitRecoverySourceRead> Sources,
    IReadOnlyList<OmsPrivateEventEnvelope> Observations, IReadOnlyList<DeribitRecoveryQueryProblem> Problems,
    int? HttpStatusCode = null)
{
    public bool TraversalComplete => Problems.Count == 0 && Sources.Count == 5 && Sources.All(x => x.Exhausted);
    // Deribit rejects historical=true together with include_unfilled=true.
    public bool HistoricalUnfilledOrdersCovered => false;
    public bool OrderHistoryComplete => false;
    // Sequential REST reads plus delayed historical indexing do not establish a coherent cut.
    public bool CoherentRecovery => false;
}

public interface IDeribitRecoveryQueries
{
    Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery query, CancellationToken ct = default);
}
