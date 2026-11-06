using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitRecoveryClockRequest(AccountKey Scope, Guid Epoch);
public sealed record DeribitRecoveryClockObservation(AccountKey Scope, Guid Epoch,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ReceivedAtUtc, DateTimeOffset? VenueTimeUtc);
public interface IDeribitRecoveryClockReader
{
    Task<DeribitRecoveryClockObservation> ReadAsync(DeribitRecoveryClockRequest request, CancellationToken ct = default);
}
