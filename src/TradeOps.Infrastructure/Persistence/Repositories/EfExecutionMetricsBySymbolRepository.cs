using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfExecutionMetricsBySymbolRepository(TradeOpsDbContext dbContext)
    : IExecutionMetricsBySymbolRepository
{
    public async Task<ExecutionMetricsBySymbolSnapshot> GetAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH signal_counts AS (
                SELECT
                    signal."Symbol" AS symbol,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Accepted') AS accepted_signals,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Rejected') AS rejected_signals,
                    COUNT(*) FILTER (WHERE signal."Outcome" = 'Received') AS pending_signals
                FROM "TradingSignals" AS signal
                WHERE signal."CreatedAt" >= @fromInclusive
                  AND signal."CreatedAt" < @toExclusive
                GROUP BY signal."Symbol"
            ),
            lifecycle_counts AS (
                SELECT
                    order_row."Symbol" AS symbol,
                    COUNT(*) AS lifecycle_events,
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
                JOIN "Orders" AS order_row
                    ON order_row."Id" = lifecycle."OrderId"
                WHERE lifecycle."OccurredAt" >= @fromInclusive
                  AND lifecycle."OccurredAt" < @toExclusive
                GROUP BY order_row."Symbol"
            ),
            fill_counts AS (
                SELECT
                    order_row."Symbol" AS symbol,
                    COUNT(*) AS fills_received
                FROM "Fills" AS fill
                JOIN "Orders" AS order_row
                    ON order_row."Id" = fill."OrderId"
                WHERE fill."FilledAt" >= @fromInclusive
                  AND fill."FilledAt" < @toExclusive
                GROUP BY order_row."Symbol"
            ),
            combined AS (
                SELECT
                    COALESCE(signal.symbol, lifecycle.symbol, fill.symbol) AS symbol,
                    COALESCE(signal.accepted_signals, 0) AS accepted_signals,
                    COALESCE(signal.rejected_signals, 0) AS rejected_signals,
                    COALESCE(signal.pending_signals, 0) AS pending_signals,
                    COALESCE(lifecycle.lifecycle_events, 0) AS lifecycle_events,
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
                FROM signal_counts AS signal
                FULL OUTER JOIN lifecycle_counts AS lifecycle
                    ON lifecycle.symbol = signal.symbol
                FULL OUTER JOIN fill_counts AS fill
                    ON fill.symbol = COALESCE(signal.symbol, lifecycle.symbol)
            )
            SELECT
                symbol,
                accepted_signals,
                rejected_signals,
                pending_signals,
                lifecycle_events,
                orders_touched,
                created,
                submitted,
                accepted,
                partially_filled,
                filled,
                cancelled,
                rejected,
                unknown,
                fills_received
            FROM combined
            ORDER BY
                accepted_signals + rejected_signals + pending_signals + lifecycle_events + fills_received DESC,
                symbol ASC
            LIMIT @fetchLimit;
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
            command.Parameters.Add(new NpgsqlParameter("@fetchLimit", NpgsqlDbType.Integer)
            {
                Value = limit + 1
            });

            var items = new List<ExecutionMetricsBySymbolItem>(limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var acceptedSignals = reader.GetInt64(1);
                var rejectedSignals = reader.GetInt64(2);
                var pendingSignals = reader.GetInt64(3);

                items.Add(new ExecutionMetricsBySymbolItem(
                    reader.GetString(0),
                    new WindowSignalExecutionMetrics(
                        acceptedSignals + rejectedSignals + pendingSignals,
                        acceptedSignals,
                        rejectedSignals,
                        pendingSignals),
                    new WindowOrderLifecycleMetrics(
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13)),
                    reader.GetInt64(14)));
            }

            var isTruncated = items.Count > limit;
            if (isTruncated)
            {
                items.RemoveAt(items.Count - 1);
            }

            return new ExecutionMetricsBySymbolSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                limit,
                isTruncated,
                items);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }
}
