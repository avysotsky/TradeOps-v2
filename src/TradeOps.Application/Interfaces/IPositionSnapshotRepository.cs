using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IPositionSnapshotRepository
{
    Task AddRangeAsync(
        IReadOnlyCollection<PositionSnapshot> snapshots,
        CancellationToken cancellationToken = default);
}
