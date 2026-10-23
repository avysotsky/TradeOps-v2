using System.Data;
using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

/// <summary>
/// Durable, synthetic-only reservations. These are NOT orders or fills in
/// production tables; no broker, payment, Worker or API paths call this.
/// This class requires an explicitly constructed ScopedReservationsDbContext.\n/// INSERT..ON CONFLICT DO NOTHING lets PostgreSQL unique constraints
/// arbitrate concurrent requests, then the committed immutable row is
/// compared with the complete incoming payload.
/// </summary>
public sealed class EfSyntheticScopedReservationJournal(ScopedReservationsDbContext db)
{
    public async Task<SyntheticReservationResult> ReserveOrderAsync(
        ScopedOrderFingerprint item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Reuses the TO-V2-20 fail-closed validation. Never write first.
        BrokerScopedIdempotencyPolicy.CompareOrders(item, item);

        if (item.BrokerOrderId is null)
        {
            return new(SyntheticReservationDisposition.RequiresProof,
                "MissingExistingBrokerBinding");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""SyntheticScopedOrderReservations""
                (""ProviderKey"", ""AccountKey"", ""ClientOrderId"", ""LocalOrderId"",
                 ""BrokerOrderId"", ""InstrumentKey"", ""Side"", ""OrderType"",
                 ""RequestedQuantity"", ""Price"", ""Classification"", ""ReservedAt"")
                VALUES ({item.ProviderKey}, {item.AccountKey}, {item.ClientOrderId},
                        {item.LocalOrderId}, {item.BrokerOrderId},
                        {item.InstrumentKey}, {item.Side.ToString()},
                        {item.OrderType.ToString()}, {item.RequestedQuantity},
                        {item.Price}, 'SyntheticOnly', now())
                ON CONFLICT DO NOTHING",
            cancellationToken);

        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(SyntheticReservationDisposition.Inserted,
                "NewScopedOrderReservation");
        }

        if (inserted != 0)
        {
            throw Invalid();
        }

        // Query by every unique collision surface. It is NOT safe to read
        // just the cOID: a duplicate local GUID or broker ID might own
        // another cOID. Multiple hits indicate conflicting reservations.
        var existing = await db.SyntheticScopedOrderReservations.AsNoTracking()
            .Where(x =>
                x.LocalOrderId == item.LocalOrderId
                || (x.ProviderKey == item.ProviderKey
                    && x.AccountKey == item.AccountKey
                    && (x.ClientOrderId == item.ClientOrderId
                        || x.BrokerOrderId == item.BrokerOrderId)))
            .Take(3)
            .ToArrayAsync(cancellationToken);

        if (existing.Length != 1)
        {
            return new(SyntheticReservationDisposition.IdentityConflict,
                "MultipleOrMissingConflictingOrderReservations");
        }

        var row = existing[0];
        if (row.Classification != "SyntheticOnly"
            || !Enum.TryParse<OrderSide>(row.Side, false, out var side)
            || !Enum.IsDefined(side)
            || !Enum.TryParse<OrderType>(row.OrderType, false, out var kind)
            || !Enum.IsDefined(kind))
        {
            throw Invalid();
        }

        var previous = new ScopedOrderFingerprint(
            row.ProviderKey, row.AccountKey, row.LocalOrderId,
            row.ClientOrderId, row.BrokerOrderId, row.InstrumentKey,
            side, kind, row.RequestedQuantity, row.Price);

        var decision = BrokerScopedIdempotencyPolicy.CompareOrders(
            previous, item);

        return new(
            decision.Disposition == ScopedIdempotencyDisposition.EquivalentReplay
                ? SyntheticReservationDisposition.EquivalentReplay
                : decision.Disposition == ScopedIdempotencyDisposition.BrokerBindingRequiresReview
                    ? SyntheticReservationDisposition.RequiresProof
                    : SyntheticReservationDisposition.IdentityConflict,
            decision.ReasonCode);
    }

    public async Task<SyntheticReservationResult> ReserveExecutionAsync(
        ScopedExecutionFingerprint item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        BrokerScopedIdempotencyPolicy.CompareExecutions(item, item);

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // Check the full parent identity, including the broker ID,
        // BEFORE the insert. The five-column FK repeats this invariant
        // independently in PostgreSQL and protects concurrent writers.
        var ownerExists = await db.SyntheticScopedOrderReservations.AsNoTracking()
            .AnyAsync(x => x.ProviderKey == item.ProviderKey
                && x.AccountKey == item.AccountKey
                && x.ClientOrderId == item.ClientOrderId
                && x.LocalOrderId == item.LocalOrderId
                && x.BrokerOrderId == item.BrokerOrderId
                && x.Classification == "SyntheticOnly",
                cancellationToken);

        if (!ownerExists)
        {
            return new(SyntheticReservationDisposition.IdentityConflict,
                "ExactScopedOrderBindingMissing");
        }

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""SyntheticScopedExecutionReservations""
                (""ProviderKey"", ""AccountKey"", ""ExecutionId"",
                 ""ClientOrderId"", ""LocalOrderId"", ""BrokerOrderId"",
                 ""Quantity"", ""Price"", ""Fee"", ""FeeCurrency"",
                 ""Classification"", ""ReservedAt"")
                VALUES ({item.ProviderKey}, {item.AccountKey},
                        {item.ExecutionId}, {item.ClientOrderId},
                        {item.LocalOrderId}, {item.BrokerOrderId},
                        {item.Quantity}, {item.Price}, {item.Fee},
                        {item.FeeCurrency}, 'SyntheticOnly', now())
                ON CONFLICT DO NOTHING",
            cancellationToken);

        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(SyntheticReservationDisposition.Inserted,
                "NewScopedExecutionReservation");
        }

        if (inserted != 0)
        {
            throw Invalid();
        }

        var existing = await db.SyntheticScopedExecutionReservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProviderKey == item.ProviderKey
                && x.AccountKey == item.AccountKey
                && x.ExecutionId == item.ExecutionId,
                cancellationToken);

        if (existing is null || existing.Classification != "SyntheticOnly")
        {
            throw Invalid();
        }

        var previous = new ScopedExecutionFingerprint(
            existing.ProviderKey, existing.AccountKey,
            existing.LocalOrderId, existing.ClientOrderId,
            existing.BrokerOrderId, existing.ExecutionId,
            existing.Quantity, existing.Price, existing.Fee,
            existing.FeeCurrency);

        var decision = BrokerScopedIdempotencyPolicy.CompareExecutions(
            previous, item);

        return new(
            decision.Disposition == ScopedIdempotencyDisposition.EquivalentReplay
                ? SyntheticReservationDisposition.EquivalentReplay
                : SyntheticReservationDisposition.IdentityConflict,
            decision.ReasonCode);
    }

    private static InvalidOperationException Invalid() =>
        new("Conflicting scoped synthetic reservation state; no broker order or fill may be emitted.");
}

public enum SyntheticReservationDisposition
{
    Inserted = 1,
    EquivalentReplay = 2,
    IdentityConflict = 3,
    RequiresProof = 4
}

public sealed record SyntheticReservationResult(
    SyntheticReservationDisposition Disposition,
    string ReasonCode);
