using Microsoft.EntityFrameworkCore;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

/// <summary>
/// Explicitly scoped SYNTHETIC local order projection. This cannot
/// attest to broker ownership; the source is the local PostgreSQL DB,
/// never the venue. No DI registration, no write method, no broker calls.
/// </summary>
public sealed record SyntheticScopedOrderCandidate(
    Guid OrderId,
    string ProviderKey,
    string AccountKey,
    string InstrumentKey,
    string ClientOrderId,
    string? BrokerOrderId,
    string Symbol,
    OrderSide Side,
    OrderStatus LocalStatus,
    decimal RequestedQuantity,
    decimal FilledQuantity);

public sealed class EfSyntheticScopedOrderReader(TradeOpsDbContext database)
{
    private const int MaxPerRead = 256;

    public async Task<IReadOnlyList<SyntheticScopedOrderCandidate>>
        GetReconciliationCandidatesAsync(
            string providerKey,
            string accountKey,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // These are deliberately the ONLY permitted stage-19 sources.
        // Genuine brokers must not accidentally inherit synthetic trust.
        if (!string.Equals(providerKey, "Mock", StringComparison.Ordinal)
            && !string.Equals(providerKey, "IbkrOffline", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        if (string.IsNullOrWhiteSpace(accountKey)
            || accountKey.Length > 128
            || accountKey != accountKey.Trim()
            || accountKey.Any(char.IsControl))
        {
            throw Invalid();
        }

        // JOIN is always by stable local GUID, never globally ambiguous
        // cOID, ticker, exchange order ID or inferred provider/account.
        var joined = await database.BrokerOrderIdentities.AsNoTracking()
            .Where(scope => scope.ProviderKey == providerKey
                && scope.AccountKey == accountKey)
            .Join(database.Orders.AsNoTracking(),
                scope => scope.OrderId,
                order => order.Id,
                (scope, order) => new { scope, order })
            .OrderBy(item => item.order.CreatedAt)
            .ThenBy(item => item.order.Id)
            .Take(MaxPerRead + 1)
            .ToArrayAsync(cancellationToken);

        if (joined.Length > MaxPerRead)
        {
            throw Invalid();
        }

        var candidates = new List<SyntheticScopedOrderCandidate>();
        foreach (var item in joined)
        {
            var candidate = ProjectCheckedSyntheticScope(
                item.scope, item.order, providerKey, accountKey);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Fail-closed LOCAL row consistency check, independently unit-testable
    /// without a database. Does NOT authenticate any real broker evidence.
    /// Null means the local order is terminal and not a shadow candidate.
    /// </summary>
    public static SyntheticScopedOrderCandidate? ProjectCheckedSyntheticScope(
        BrokerOrderIdentity scope, Order order,
        string providerKey, string accountKey)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(order);

        if (!(providerKey is "Mock" or "IbkrOffline")
            || string.IsNullOrWhiteSpace(accountKey)
            || accountKey.Length > 128
            || accountKey != accountKey.Trim()
            || accountKey.Any(char.IsControl)
            || !Enum.IsDefined(order.Side)
            || !Enum.IsDefined(order.Status)
            || order.RequestedQuantity <= 0m
            || order.FilledQuantity < 0m
            || order.FilledQuantity > order.RequestedQuantity)
        {
            throw Invalid();
        }

        if (scope.OrderId != order.Id
            || !string.Equals(scope.ProviderKey, providerKey,
                StringComparison.Ordinal)
            || !string.Equals(scope.AccountKey, accountKey,
                StringComparison.Ordinal)
            || !string.Equals(scope.Classification,
                BrokerOrderIdentity.SyntheticClassification,
                StringComparison.Ordinal)
            || !string.Equals(scope.ClientOrderId, order.ClientOrderId,
                StringComparison.Ordinal)
            || !string.Equals(scope.BrokerOrderId, order.ExchangeOrderId,
                StringComparison.Ordinal)
            || !string.Equals(
                scope.InstrumentKey,
                (providerKey == "Mock"
                    ? "MOCK:"
                    : "SYNTHETIC-IBKR-SYMBOL:") + order.Symbol,
                StringComparison.Ordinal))
        {
            throw Invalid();
        }

        if (order.Status is not (OrderStatus.Submitted
            or OrderStatus.Accepted
            or OrderStatus.PartiallyFilled
            or OrderStatus.Unknown))
        {
            return null;
        }

        return new SyntheticScopedOrderCandidate(
            order.Id, scope.ProviderKey, scope.AccountKey,
            scope.InstrumentKey, order.ClientOrderId,
            scope.BrokerOrderId, order.Symbol, order.Side, order.Status,
            order.RequestedQuantity, order.FilledQuantity);
    }

    private static InvalidOperationException Invalid() =>
        new("Synthetic broker scope is missing, contradictory or exceeds limits; no reconciliation candidate emitted.");
}
