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

public sealed class EfSignalTransitionMetricsRepository(TradeOpsDbContext dbContext)
    : ISignalTransitionMetricsRepository
{
    public async Task<SignalTransitionMetricsWindowSnapshot> GetWindowAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TradingSignalOutcomeEvent> query = dbContext.TradingSignalOutcomeEvents
            .AsNoTracking()
            .Where(item =>
                item.OccurredAt >= fromInclusive &&
                item.OccurredAt < toExclusive);

        if (symbol is not null)
        {
            query =
                from item in query
                join signal in dbContext.TradingSignals.AsNoTracking()
                    on item.TradingSignalId equals signal.Id
                where signal.Symbol == symbol
                select item;
        }

        var counts = await query
            .GroupBy(item => item.Outcome)
            .Select(group => new
            {
                Outcome = group.Key,
                Count = group.LongCount()
            })
            .ToDictionaryAsync(
                item => item.Outcome,
                item => item.Count,
                cancellationToken);

        return new SignalTransitionMetricsWindowSnapshot(
            DateTimeOffset.UtcNow,
            fromInclusive,
            toExclusive,
            symbol,
            counts.GetValueOrDefault(SignalOutcome.Received),
            counts.GetValueOrDefault(SignalOutcome.Accepted),
            counts.GetValueOrDefault(SignalOutcome.Rejected));
    }

    public async Task<SignalTransitionMetricsSeriesSnapshot> GetSeriesAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string bucket,
        TimeSpan bucketSize,
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var signalJoin = symbol is null
            ? string.Empty
            : "JOIN \"TradingSignals\" AS signal ON signal.\"Id\" = transition.\"TradingSignalId\"";
        var symbolPredicate = symbol is null
            ? string.Empty
            : "AND signal.\"Symbol\" = @symbol";

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
            transition_counts AS (
                SELECT
                    date_bin(
                        @bucketSize,
                        transition."OccurredAt",
                        TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                    COUNT(*) FILTER (WHERE transition."Outcome" = 'Received') AS received,
                    COUNT(*) FILTER (WHERE transition."Outcome" = 'Accepted') AS accepted,
                    COUNT(*) FILTER (WHERE transition."Outcome" = 'Rejected') AS rejected
                FROM "TradingSignalOutcomeEvents" AS transition
                {signalJoin}
                WHERE transition."OccurredAt" >= @fromInclusive
                  AND transition."OccurredAt" < @toExclusive
                  {symbolPredicate}
                GROUP BY 1
            )
            SELECT
                GREATEST(bucket.bucket_start, @fromInclusive) AS from_inclusive,
                LEAST(bucket.bucket_start + @bucketSize, @toExclusive) AS to_exclusive,
                COALESCE(transition.received, 0) AS received,
                COALESCE(transition.accepted, 0) AS accepted,
                COALESCE(transition.rejected, 0) AS rejected
            FROM buckets AS bucket
            LEFT JOIN transition_counts AS transition
                ON transition.bucket_start = bucket.bucket_start
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

            var buckets = new List<SignalTransitionMetricsSeriesBucket>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                buckets.Add(new SignalTransitionMetricsSeriesBucket(
                    ReadUtcTimestamp(reader, 0),
                    ReadUtcTimestamp(reader, 1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            return new SignalTransitionMetricsSeriesSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                bucket,
                symbol,
                buckets);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task<SignalTransitionMetricsBySymbolSnapshot> GetBySymbolAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                signal."Symbol" AS symbol,
                COUNT(*) AS total_transitions,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Received') AS received,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Rejected') AS rejected
            FROM "TradingSignalOutcomeEvents" AS transition
            JOIN "TradingSignals" AS signal
                ON signal."Id" = transition."TradingSignalId"
            WHERE transition."OccurredAt" >= @fromInclusive
              AND transition."OccurredAt" < @toExclusive
            GROUP BY signal."Symbol"
            ORDER BY total_transitions DESC, symbol ASC
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

            var items = new List<SignalTransitionMetricsBySymbolItem>(limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new SignalTransitionMetricsBySymbolItem(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            var isTruncated = items.Count > limit;
            if (isTruncated)
            {
                items.RemoveAt(items.Count - 1);
            }

            return new SignalTransitionMetricsBySymbolSnapshot(
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

    private static DateTimeOffset ReadUtcTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
