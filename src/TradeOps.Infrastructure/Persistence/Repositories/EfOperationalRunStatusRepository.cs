using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfOperationalRunStatusRepository(TradeOpsDbContext dbContext)
    : IOperationalRunStatusRepository
{
    public Task<OperationalRunStatus?> GetAsync(
        string runType,
        CancellationToken cancellationToken = default)
    {
        return dbContext.OperationalRunStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.RunType == runType, cancellationToken);
    }

    public async Task MarkStartedAsync(
        string runType,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new OperationalRunRecord
        {
            Id = Guid.NewGuid(),
            RunType = runType,
            StartedAt = now,
            IsRunning = true,
            UpdatedAt = now
        };
        dbContext.OperationalRuns.Add(run);

        var status = await dbContext.OperationalRunStatuses
            .FirstOrDefaultAsync(item => item.RunType == runType, cancellationToken);

        if (status is null)
        {
            status = new OperationalRunStatus
            {
                RunType = runType,
                LatestRunId = run.Id,
                StartedAt = now,
                IsRunning = true,
                UpdatedAt = now
            };
            dbContext.OperationalRunStatuses.Add(status);
        }
        else
        {
            status.LatestRunId = run.Id;
            status.StartedAt = now;
            status.CompletedAt = null;
            status.IsRunning = true;
            status.Succeeded = null;
            status.OrdersScanned = 0;
            status.OrdersUpdated = 0;
            status.OrderIssues = 0;
            status.OrdersMissingOnExchange = 0;
            status.PositionsCompared = 0;
            status.PositionMismatches = 0;
            status.PositionSnapshots = 0;
            status.ErrorMessage = null;
            status.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(
        string runType,
        OperationalRunMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        var status = await GetTrackedOrCreateAsync(runType, cancellationToken);
        var run = await GetTrackedRunOrCreateAsync(status, runType, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        ApplyCompleted(status, metrics, now);
        ApplyCompleted(run, metrics, now);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        string runType,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        var status = await GetTrackedOrCreateAsync(runType, cancellationToken);
        var run = await GetTrackedRunOrCreateAsync(status, runType, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var truncatedError = Truncate(errorMessage, 1000);

        status.CompletedAt = now;
        status.IsRunning = false;
        status.Succeeded = false;
        status.ErrorMessage = truncatedError;
        status.UpdatedAt = now;

        run.CompletedAt = now;
        run.IsRunning = false;
        run.Succeeded = false;
        run.ErrorMessage = truncatedError;
        run.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<OperationalRunStatus> GetTrackedOrCreateAsync(
        string runType,
        CancellationToken cancellationToken)
    {
        var status = await dbContext.OperationalRunStatuses
            .FirstOrDefaultAsync(item => item.RunType == runType, cancellationToken);

        if (status is not null)
        {
            return status;
        }

        var now = DateTimeOffset.UtcNow;
        status = new OperationalRunStatus
        {
            RunType = runType,
            StartedAt = now,
            IsRunning = true,
            UpdatedAt = now
        };
        dbContext.OperationalRunStatuses.Add(status);
        return status;
    }

    private async Task<OperationalRunRecord> GetTrackedRunOrCreateAsync(
        OperationalRunStatus status,
        string runType,
        CancellationToken cancellationToken)
    {
        if (status.LatestRunId is Guid runId)
        {
            var existing = await dbContext.OperationalRuns
                .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);

            if (existing is not null)
            {
                return existing;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var run = new OperationalRunRecord
        {
            Id = Guid.NewGuid(),
            RunType = runType,
            StartedAt = status.StartedAt == default ? now : status.StartedAt,
            IsRunning = true,
            UpdatedAt = now
        };
        dbContext.OperationalRuns.Add(run);
        status.LatestRunId = run.Id;
        return run;
    }

    private static void ApplyCompleted(
        OperationalRunStatus status,
        OperationalRunMetrics metrics,
        DateTimeOffset now)
    {
        status.CompletedAt = now;
        status.IsRunning = false;
        status.Succeeded = true;
        status.OrdersScanned = metrics.OrdersScanned;
        status.OrdersUpdated = metrics.OrdersUpdated;
        status.OrderIssues = metrics.OrderIssues;
        status.OrdersMissingOnExchange = metrics.OrdersMissingOnExchange;
        status.PositionsCompared = metrics.PositionsCompared;
        status.PositionMismatches = metrics.PositionMismatches;
        status.PositionSnapshots = metrics.PositionSnapshots;
        status.ErrorMessage = null;
        status.UpdatedAt = now;
    }

    private static void ApplyCompleted(
        OperationalRunRecord run,
        OperationalRunMetrics metrics,
        DateTimeOffset now)
    {
        run.CompletedAt = now;
        run.IsRunning = false;
        run.Succeeded = true;
        run.OrdersScanned = metrics.OrdersScanned;
        run.OrdersUpdated = metrics.OrdersUpdated;
        run.OrderIssues = metrics.OrderIssues;
        run.OrdersMissingOnExchange = metrics.OrdersMissingOnExchange;
        run.PositionsCompared = metrics.PositionsCompared;
        run.PositionMismatches = metrics.PositionMismatches;
        run.PositionSnapshots = metrics.PositionSnapshots;
        run.ErrorMessage = null;
        run.UpdatedAt = now;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
