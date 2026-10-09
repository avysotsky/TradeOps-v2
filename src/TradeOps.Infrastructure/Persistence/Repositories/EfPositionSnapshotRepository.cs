using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfPositionSnapshotRepository(TradeOpsDbContext dbContext)
    : IPositionSnapshotRepository
{
    public async Task AddRangeAsync(
        IReadOnlyCollection<PositionSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        dbContext.PositionSnapshots.AddRange(snapshots);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
