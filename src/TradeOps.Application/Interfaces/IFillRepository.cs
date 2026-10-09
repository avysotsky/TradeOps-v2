using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IFillRepository
{
    Task<bool> TryAddAsync(
        Fill fill,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PositionFill>> GetPositionFillsAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);
}
