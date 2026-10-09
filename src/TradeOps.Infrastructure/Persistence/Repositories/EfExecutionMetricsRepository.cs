using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfExecutionMetricsRepository(
    TradeOpsDbContext dbContext,
    RiskSettings riskSettings) : IExecutionMetricsRepository
{
    public async Task<ExecutionMetricsSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var signalCounts = await dbContext.TradingSignals
            .AsNoTracking()
            .GroupBy(signal => signal.Outcome)
            .Select(group => new
            {
                Outcome = group.Key,
                Count = group.LongCount()
            })
            .ToDictionaryAsync(
                item => item.Outcome,
                item => item.Count,
                cancellationToken);

        var acceptedSignals = signalCounts.GetValueOrDefault(SignalOutcome.Accepted);
        var rejectedSignals = signalCounts.GetValueOrDefault(SignalOutcome.Rejected);
        var pendingSignals = signalCounts.GetValueOrDefault(SignalOutcome.Received);
        var receivedSignals = acceptedSignals + rejectedSignals + pendingSignals;

        var orderCounts = await dbContext.Orders
            .AsNoTracking()
            .GroupBy(order => order.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.LongCount()
            })
            .ToDictionaryAsync(
                item => item.Status,
                item => item.Count,
                cancellationToken);

        var totalOrders = orderCounts.Values.Sum();
        var fillsReceived = await dbContext.Fills
            .AsNoTracking()
            .LongCountAsync(cancellationToken);

        var persistedRiskState = await dbContext.OperationalRiskStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                state => state.Id == OperationalRiskState.DefaultId,
                cancellationToken);

        var runStatuses = await dbContext.OperationalRunStatuses
            .AsNoTracking()
            .Where(status =>
                status.RunType == OperationalRunTypes.OrderReconciliation ||
                status.RunType == OperationalRunTypes.RecoveryCycle)
            .ToArrayAsync(cancellationToken);

        var reconciliation = runStatuses.FirstOrDefault(
            status => status.RunType == OperationalRunTypes.OrderReconciliation);
        var recovery = runStatuses.FirstOrDefault(
            status => status.RunType == OperationalRunTypes.RecoveryCycle);

        return new ExecutionMetricsSnapshot(
            DateTimeOffset.UtcNow,
            new SignalExecutionMetrics(
                receivedSignals,
                acceptedSignals,
                rejectedSignals,
                pendingSignals),
            new CurrentOrderStatusMetrics(
                totalOrders,
                orderCounts.GetValueOrDefault(OrderStatus.Created),
                orderCounts.GetValueOrDefault(OrderStatus.Submitted),
                orderCounts.GetValueOrDefault(OrderStatus.Accepted),
                orderCounts.GetValueOrDefault(OrderStatus.PartiallyFilled),
                orderCounts.GetValueOrDefault(OrderStatus.Filled),
                orderCounts.GetValueOrDefault(OrderStatus.Cancelled),
                orderCounts.GetValueOrDefault(OrderStatus.Rejected),
                orderCounts.GetValueOrDefault(OrderStatus.Unknown)),
            fillsReceived,
            new ExecutionRiskMetrics(
                persistedRiskState?.TradingEnabled ?? riskSettings.TradingEnabled,
                persistedRiskState?.EmergencyStop ?? riskSettings.EmergencyStop),
            new OperationalExecutionMetrics(
                reconciliation?.Succeeded,
                recovery?.Succeeded,
                recovery?.PositionMismatches));
    }

    public async Task<ExecutionMetricsWindowSnapshot> GetWindowAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var signalQuery = BuildSignalQuery(fromInclusive, toExclusive, symbol);
        var signalCounts = await signalQuery
            .GroupBy(signal => signal.Outcome)
            .Select(group => new
            {
                Outcome = group.Key,
                Count = group.LongCount()
            })
            .ToDictionaryAsync(
                item => item.Outcome,
                item => item.Count,
                cancellationToken);

        var acceptedSignals = signalCounts.GetValueOrDefault(SignalOutcome.Accepted);
        var rejectedSignals = signalCounts.GetValueOrDefault(SignalOutcome.Rejected);
        var pendingSignals = signalCounts.GetValueOrDefault(SignalOutcome.Received);
        var receivedSignals = acceptedSignals + rejectedSignals + pendingSignals;

        var lifecycleQuery = BuildLifecycleQuery(fromInclusive, toExclusive, symbol);
        var lifecycleCounts = await lifecycleQuery
            .GroupBy(item => item.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.LongCount()
            })
            .ToDictionaryAsync(
                item => item.Status,
                item => item.Count,
                cancellationToken);

        var lifecycleEvents = lifecycleCounts.Values.Sum();
        var ordersTouched = await lifecycleQuery
            .Select(item => item.OrderId)
            .Distinct()
            .LongCountAsync(cancellationToken);

        var fillQuery = BuildFillQuery(fromInclusive, toExclusive, symbol);
        var fillsReceived = await fillQuery.LongCountAsync(cancellationToken);

        return new ExecutionMetricsWindowSnapshot(
            DateTimeOffset.UtcNow,
            fromInclusive,
            toExclusive,
            symbol,
            new WindowSignalExecutionMetrics(
                receivedSignals,
                acceptedSignals,
                rejectedSignals,
                pendingSignals),
            new WindowOrderLifecycleMetrics(
                lifecycleEvents,
                ordersTouched,
                lifecycleCounts.GetValueOrDefault(OrderStatus.Created),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Submitted),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Accepted),
                lifecycleCounts.GetValueOrDefault(OrderStatus.PartiallyFilled),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Filled),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Cancelled),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Rejected),
                lifecycleCounts.GetValueOrDefault(OrderStatus.Unknown)),
            fillsReceived);
    }

    public async Task<ExecutionMetricsSeriesSnapshot> GetSeriesAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string bucket,
        TimeSpan bucketSize,
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var signalSymbolPredicate = symbol is null
            ? string.Empty
            : "AND signal.\"Symbol\" = @symbol";
        var lifecycleOrderJoin = symbol is null
            ? string.Empty
            : "JOIN \"Orders\" AS lifecycle_order ON lifecycle_order.\"Id\" = lifecycle.\"OrderId\"";
        var lifecycleSymbolPredicate = symbol is null
            ? string.Empty
            : "AND lifecycle_order.\"Symbol\" = @symbol";
        var fillOrderJoin = symbol is null
            ? string.Empty
            : "JOIN \"Orders\" AS fill_order ON fill_order.\"Id\" = fill.\"OrderId\"";
        var fillSymbolPredicate = symbol is null
            ? string.Empty
            : "AND fill_order.\"Symbol\" = @symbol";

        var sql = $"""
            WITH buckets AS (
                SELECT bucket_start
                FROM generate_series(
                    date_bin(
                        @bucketSize,
                        @fromInclusive,
                        TIMESTAMPTZ '1970-01-01 00:00:00+00'),
                    @toExclusive,
                    @bucketSize) AS bucket_start
                WHERE bucket_start < @toExclusive
            ),
            signal_counts AS (
                SELECT
                    date_bin(
                        @bucketSize,
                        signal."CreatedAt",
                        TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Accepted') AS accepted,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Rejected') AS rejected,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Received') AS pending
                FROM "TradingSignals" AS signal
                WHERE signal."CreatedAt" >= @fromInclusive
                  AND signal."CreatedAt" < @toExclusive
                  {signalSymbolPredicate}
                GROUP BY 1
            ),
            lifecycle_counts AS (
                SELECT
                    date_bin(
                        @bucketSize,
                        lifecycle."OccurredAt",
                        TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                    COUNT(*) AS events,
                    COUNT(DISTINCT lifecycle."OrderId") AS orders_touched,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Created') AS created,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Submitted') AS submitted,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Accepted') AS accepted,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'PartiallyFilled') AS partially_filled,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Filled') AS filled,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Cancelled') AS cancelled,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Rejected') AS rejected,
                    COUNT(*) FILTER (WHERE lifecycle."Status" = 'Unknown') AS unknown
                FROM "OrderLifecycleEvents" AS lifecycle
                {lifecycleOrderJoin}
                WHERE lifecycle."OccurredAt" >= @fromInclusive
                  AND lifecycle."OccurredAt" < @toExclusive
                  {lifecycleSymbolPredicate}
                GROUP BY 1
            ),
            fill_counts AS (
                SELECT
                    date_bin(
                        @bucketSize,
                        fill."FilledAt",
                        TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                    COUNT(*) AS fills_received
                FROM "Fills" AS fill
                {fillOrderJoin}
                WHERE fill."FilledAt" >= @fromInclusive
                  AND fill."FilledAt" < @toExclusive
                  {fillSymbolPredicate}
                GROUP BY 1
            )
            SELECT
                GREATEST(bucket.bucket_start, @fromInclusive) AS from_inclusive,
                LEAST(bucket.bucket_start + @bucketSize, @toExclusive) AS to_exclusive,
                COALESCE(signal.accepted, 0) AS accepted_signals,
                COALESCE(signal.rejected, 0) AS rejected_signals,
                COALESCE(signal.pending, 0) AS pending_signals,
                COALESCE(lifecycle.events, 0) AS lifecycle_events,
                COALESCE(lifecycle.orders_touched, 0) AS orders_touched,
                COALESCE(lifecycle.created, 0) AS created,
                COALESCE(lifecycle.submitted, 0) AS submitted,
                COALESCE(lifecycle.accepted, 0) AS accepted,
                COALESCE(lifecycle.partially_filled, 0) AS partially_filled,
                COALESCE(lifecycle.filled, 0) AS filled,
                COALESCE(lifecycle.cancelled, 0) AS cancelled,
                COALESCE(lifecycle.rejected, 0) AS rejected,
                COALESCE(lifecycle.unknown, 0) AS unknown,
                COALESCE(fill.fills_received, 0) AS fills_received
            FROM buckets AS bucket
            LEFT JOIN signal_counts AS signal
                ON signal.bucket_start = bucket.bucket_start
            LEFT JOIN lifecycle_counts AS lifecycle
                ON lifecycle.bucket_start = bucket.bucket_start
            LEFT JOIN fill_counts AS fill
                ON fill.bucket_start = bucket.bucket_start
            ORDER BY bucket.bucket_start;
            """;

        var connection = dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;

        if (shouldCloseConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new NpgsqlParameter("@fromInclusive", NpgsqlDbType.TimestampTz)
            {
                Value = fromInclusive
            });
            command.Parameters.Add(new NpgsqlParameter("@toExclusive", NpgsqlDbType.TimestampTz)
            {
                Value = toExclusive
            });
            command.Parameters.Add(new NpgsqlParameter("@bucketSize", NpgsqlDbType.Interval)
            {
                Value = bucketSize
            });

            if (symbol is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("@symbol", NpgsqlDbType.Varchar)
                {
                    Value = symbol
                });
            }

            var series = new List<ExecutionMetricsSeriesBucket>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var acceptedSignals = reader.GetInt64(2);
                var rejectedSignals = reader.GetInt64(3);
                var pendingSignals = reader.GetInt64(4);

                series.Add(new ExecutionMetricsSeriesBucket(
                    ReadUtcTimestamp(reader, 0),
                    ReadUtcTimestamp(reader, 1),
                    new WindowSignalExecutionMetrics(
                        acceptedSignals + rejectedSignals + pendingSignals,
                        acceptedSignals,
                        rejectedSignals,
                        pendingSignals),
                    new WindowOrderLifecycleMetrics(
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13),
                        reader.GetInt64(14)),
                    reader.GetInt64(15)));
            }

            return new ExecutionMetricsSeriesSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                bucket,
                symbol,
                series);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private IQueryable<TradingSignal> BuildSignalQuery(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol)
    {
        IQueryable<TradingSignal> query = dbContext.TradingSignals
            .AsNoTracking()
            .Where(signal =>
                signal.CreatedAt >= fromInclusive &&
                signal.CreatedAt < toExclusive);

        return symbol is null
            ? query
            : query.Where(signal => signal.Symbol == symbol);
    }

    private IQueryable<OrderLifecycleEvent> BuildLifecycleQuery(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol)
    {
        IQueryable<OrderLifecycleEvent> query = dbContext.OrderLifecycleEvents
            .AsNoTracking()
            .Where(item =>
                item.OccurredAt >= fromInclusive &&
                item.OccurredAt < toExclusive);

        if (symbol is null)
        {
            return query;
        }

        return
            from item in query
            join order in dbContext.Orders.AsNoTracking()
                on item.OrderId equals order.Id
            where order.Symbol == symbol
            select item;
    }

    private IQueryable<Fill> BuildFillQuery(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol)
    {
        IQueryable<Fill> query = dbContext.Fills
            .AsNoTracking()
            .Where(fill =>
                fill.FilledAt >= fromInclusive &&
                fill.FilledAt < toExclusive);

        if (symbol is null)
        {
            return query;
        }

        return
            from fill in query
            join order in dbContext.Orders.AsNoTracking()
                on fill.OrderId equals order.Id
            where order.Symbol == symbol
            select fill;
    }

    private static DateTimeOffset ReadUtcTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
