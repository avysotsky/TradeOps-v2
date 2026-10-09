using Microsoft.Extensions.Options;
using TradeOps.Application.Interfaces;

namespace TradeOps.Worker;

public sealed class ExchangeEventWorker(
    IExchangeEventStream eventStream,
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<ExchangeEventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!eventStream.IsEnabled)
        {
            logger.LogInformation("Exchange event stream is disabled for the selected provider.");
            return;
        }

        var settings = options.Value;
        var retryDelay = Math.Max(1, settings.InitialRetryDelaySeconds);
        var maxRetryDelay = Math.Max(retryDelay, settings.MaxRetryDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting exchange private event stream.");

                await eventStream.RunAsync(
                    ProcessOrderUpdateAsync,
                    ProcessExecutionUpdateAsync,
                    stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Exchange event stream ended unexpectedly.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Exchange event stream failed. Reconnecting in {RetryDelaySeconds}s.", retryDelay);
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), stoppingToken);
                retryDelay = Math.Min(retryDelay * 2, maxRetryDelay);
            }
        }

        async Task ProcessOrderUpdateAsync(TradeOps.Application.Models.ExchangeOrderUpdate update, CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IExchangeEventProcessor>();
            await processor.ProcessOrderUpdateAsync(update, cancellationToken);
        }

        async Task ProcessExecutionUpdateAsync(TradeOps.Application.Models.ExchangeExecutionUpdate update, CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IExchangeEventProcessor>();
            await processor.ProcessExecutionUpdateAsync(update, cancellationToken);
        }
    }
}
