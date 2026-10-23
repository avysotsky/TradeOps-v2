using Microsoft.EntityFrameworkCore;
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
            var scope = item.scope;
            var order = item.order;

            // A malicious/manual SQL insert with a mismatched cOID,
            // broker ID or instrument classification MUST NOT become
            // an accepted read-only reconciliation identity.
            if (!string.Equals(scope.Classification,
                    BrokerOrderIdentity.SyntheticClassification,
                    StringComparison.Ordinal)
                || scope.OrderId != order.Id
                || !string.Equals(scope.ClientOrderId, order.ClientOrderId,
                    StringComparison.Ordinal)
                || !string.Equals(scope.BrokerOrderId, order.ExchangeOrderId,
                    StringComparison.Ordinal)
                // The sidecar namespace alone is not enough. A
                // synthetically declared instrument must match the local
                // order's full symbol EXACTLY; do not silently rebind a
                // BTCUSDT order to an ETHUSDT synthetic instrument.
                || !string.Equals(
                    scope.InstrumentKey,
                    (providerKey == "Mock"
                        ? "MOCK:" : "SYNTHETIC-IBKR-SYMBOL:") + order.Symbol,
                    StringComparison.Ordinal))
            {
                throw Invalid();
            }

            if (order.Status is not (OrderStatus.Submitted
                or OrderStatus.Accepted
                or OrderStatus.PartiallyFilled
                or OrderStatus.Unknown))
            {
                continue;
            }

            candidates.Add(new SyntheticScopedOrderCandidate(
                order.Id, scope.ProviderKey, scope.AccountKey,
                scope.InstrumentKey, order.ClientOrderId,
                scope.BrokerOrderId, order.Symbol, order.Side, order.Status,
                order.RequestedQuantity, order.FilledQuantity));
        }

        return candidates.AsReadOnly();
    }

    private static InvalidOperationException Invalid() =>
        new("Synthetic broker scope is missing, contradictory or exceeds limits; no reconciliation candidate emitted.");
}
