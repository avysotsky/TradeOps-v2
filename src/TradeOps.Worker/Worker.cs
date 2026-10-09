using Microsoft.Extensions.Options;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    IExchangeConnectionManager connectionManager,
    IAlertService alertService,
    IOptions<WorkerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private bool _wasDisconnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var interval = TimeSpan.FromSeconds(Math.Max(5, settings.ReconciliationIntervalSeconds));

        logger.LogInformation(
            "TradeOps recovery worker started. Reconciliation interval: {IntervalSeconds}s.",
            interval.TotalSeconds);

        await RunRecoveryCycleWithRetryAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunRecoveryCycleWithRetryAsync(stoppingToken);
        }
    }

    private async Task RunRecoveryCycleWithRetryAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var retryDelaySeconds = Math.Max(1, settings.InitialRetryDelaySeconds);
        var maxRetryDelaySeconds = Math.Max(retryDelaySeconds, settings.MaxRetryDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var runStatusRepository = scope.ServiceProvider
                .GetRequiredService<IOperationalRunStatusRepository>();

            try
            {
                await runStatusRepository.MarkStartedAsync(
                    OperationalRunTypes.RecoveryCycle,
                    stoppingToken);

                await connectionManager.EnsureConnectedAsync(stoppingToken);

                if (_wasDisconnected)
                {
                    logger.LogInformation("Exchange connection recovered.");

                    await alertService.SendAsync(
                        new AlertMessage(
                            "ApiReconnected",
                            "TradeOps exchange connection recovered.",
                            AlertSeverity.Info),
                        stoppingToken);

                    _wasDisconnected = false;
                }

                var reconciliationService = scope.ServiceProvider
                    .GetRequiredService<IOrderReconciliationService>();
                var positionService = scope.ServiceProvider
                    .GetRequiredService<IPositionService>();
                var positionReconciliationService = scope.ServiceProvider
                    .GetRequiredService<IPositionReconciliationService>();

                var orderSummary = await reconciliationService.ReconcileAsync(stoppingToken);
                var positionSummary = await positionReconciliationService.ReconcileAsync(stoppingToken);
                var positions = await positionService.CaptureSnapshotsAsync(stoppingToken);

                await runStatusRepository.MarkCompletedAsync(
                    OperationalRunTypes.RecoveryCycle,
                    new OperationalRunMetrics(
                        OrdersScanned: orderSummary.Scanned,
                        OrdersUpdated: orderSummary.Updated,
                        OrderIssues: orderSummary.Issues.Count,
                        OrdersMissingOnExchange: orderSummary.MissingOnExchange,
                        PositionsCompared: positionSummary.SymbolsCompared,
                        PositionMismatches: positionSummary.Mismatched,
                        PositionSnapshots: positions.Count),
                    stoppingToken);

                logger.LogInformation(
                    "Recovery cycle completed. Connected={Connected}, OrdersScanned={OrdersScanned}, OrdersUpdated={OrdersUpdated}, OrderIssues={OrderIssueCount}, PositionsCompared={PositionsCompared}, PositionMismatches={PositionMismatchCount}, PositionSnapshots={PositionSnapshotCount}.",
                    connectionManager.IsConnected,
                    orderSummary.Scanned,
                    orderSummary.Updated,
                    orderSummary.Issues.Count,
                    positionSummary.SymbolsCompared,
                    positionSummary.Mismatched,
                    positions.Count);

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                try
                {
                    await runStatusRepository.MarkFailedAsync(
                        OperationalRunTypes.RecoveryCycle,
                        exception.Message,
                        CancellationToken.None);
                }
                catch (Exception statusException)
                {
                    logger.LogWarning(statusException, "Failed to persist recovery-cycle failure status.");
                }

                logger.LogError(
                    exception,
                    "Recovery cycle failed. Retrying in {RetryDelaySeconds}s.",
                    retryDelaySeconds);

                if (!_wasDisconnected)
                {
                    await alertService.SendAsync(
                        new AlertMessage(
                            "ApiDisconnected",
                            $"TradeOps recovery cycle failed: {exception.Message}",
                            AlertSeverity.Error),
                        stoppingToken);
                }

                _wasDisconnected = true;

                await Task.Delay(
                    TimeSpan.FromSeconds(retryDelaySeconds),
                    stoppingToken);

                retryDelaySeconds = Math.Min(
                    retryDelaySeconds * 2,
                    maxRetryDelaySeconds);
            }
        }
    }
}
