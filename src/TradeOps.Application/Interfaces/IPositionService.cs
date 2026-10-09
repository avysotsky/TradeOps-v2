using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IPositionService
{
    Task<IReadOnlyCollection<PositionState>> GetLocalAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PositionState>> GetCurrentAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PositionState>> CaptureSnapshotsAsync(
        CancellationToken cancellationToken = default);
}
