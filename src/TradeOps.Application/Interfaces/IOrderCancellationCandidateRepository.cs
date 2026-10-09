using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOrderCancellationCandidateRepository
{
    Task<IReadOnlyCollection<Order>> GetCancellationCandidatesAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);
}
