using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitAccountSnapshotRequest(AccountKey Scope, Guid Epoch, string Currency, string Kind,
    TimeSpan MaximumObservationSpan, int MaximumPositions = 256);

// Local request/receive times are observation bounds, never venue snapshot timestamps.
public sealed record DeribitAccountObservationWindow(DateTimeOffset RequestedAtUtc, DateTimeOffset ReceivedAtUtc);
public sealed record DeribitCurrencyBalance(string Currency, decimal CashBalance, decimal Equity,
    bool CrossCollateralEnabled, bool PortfolioMarginingEnabled, DeribitAccountObservationWindow Window);
public sealed record DeribitAccountPosition(string NativeInstrument, string Kind, string Direction,
    decimal NativeSize, string SizeCurrency, decimal? BaseSize, string BaseCurrency);

public enum DeribitAccountSnapshotProblem
{
    Authentication, AccountMismatch, Protocol, Transport, Capacity, Deadline, UnmappedInstrument, StaleObservation
}

/// <summary>In-memory scoped observations only; absence of a row is not a synthesized zero position.</summary>
public sealed record DeribitAccountSnapshotResult(AccountKey Scope, Guid Epoch, string Currency, string Kind,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, TimeSpan MaximumObservationSpan,
    IReadOnlyList<string> RequestedInstruments, DeribitCurrencyBalance? Balance,
    DeribitAccountObservationWindow? PositionsWindow, IReadOnlyList<DeribitAccountPosition> Positions,
    IReadOnlyList<DeribitAccountSnapshotProblem> Problems)
{
    public bool QueriesCompleted => Problems.Count == 0 && Balance is not null && PositionsWindow is not null;
    // A consumer must evaluate this at use time; successful I/O never confers permanent freshness.
    public bool IsFreshAt(DateTimeOffset now) => QueriesCompleted && now >= CompletedAtUtc && now >= StartedAtUtc &&
        now - StartedAtUtc <= MaximumObservationSpan;
    public bool AccountWideCoverage => false;
    public bool VenueCoherent => false;
    public bool AccountReady => false;
    public bool DurableProjectionPublished => false;
}

public interface IDeribitAccountSnapshotReader
{
    Task<DeribitAccountSnapshotResult> ReadAsync(DeribitAccountSnapshotRequest request, CancellationToken ct = default);
}
