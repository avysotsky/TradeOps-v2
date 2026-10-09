using Microsoft.Extensions.Options;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Worker;

public sealed class TradingViewDeliveryHealthWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<TradingViewDeliveryHealthSettings> options,
    ILogger<TradingViewDeliveryHealthWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation(
                "TradingView delivery health monitoring is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(
            settings.EvaluationIntervalSeconds);

        logger.LogInformation(
            "TradingView delivery health worker started. Interval={IntervalSeconds}s, Lookback={LookbackMinutes}m.",
            settings.EvaluationIntervalSeconds,
            settings.LookbackMinutes);

        await EvaluateSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await EvaluateSafelyAsync(stoppingToken);
        }
    }

    private async Task EvaluateSafelyAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var evaluator = scope.ServiceProvider
                .GetRequiredService<ITradingViewDeliveryHealthEvaluator>();

            var evaluation = await evaluator.EvaluateAsync(
                stoppingToken);

            logger.LogInformation(
                "TradingView delivery health evaluated. Status={Status}, Total={Total}, Failed={Failed}, Conflict={Conflict}, RiskRejected={RiskRejected}, AverageLatencyMs={AverageLatencyMs}.",
                evaluation.Status,
                evaluation.Metrics.Total,
                evaluation.Metrics.Failed,
                evaluation.Metrics.Conflict,
                evaluation.Metrics.RiskRejected,
                evaluation.Metrics.AverageLatencyMilliseconds);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "TradingView delivery health evaluation failed.");
        }
    }
}
