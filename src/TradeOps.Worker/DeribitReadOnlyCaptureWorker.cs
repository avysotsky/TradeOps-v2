using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

/// <summary>Capture-only runtime. Terminal failures stop the host; it never announces B4 readiness.</summary>
public sealed class DeribitReadOnlyCaptureWorker(DeribitPrivateCaptureBinding binding, NpgsqlDataSource source,
    DeribitPrivateWebSocketStream stream, ILogger<DeribitReadOnlyCaptureWorker> logger) : BackgroundService
{
    private OmsPrivateCaptureCoordinator? coordinator;
    public OmsPrivateStreamStatus? Status => Volatile.Read(ref coordinator)?.Status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var owner = await OmsPostgresPrivateCaptureOwner.AcquireAsync(source, binding.Scope, stoppingToken);
            await using var active = new OmsPrivateCaptureCoordinator(owner);
            Volatile.Write(ref coordinator, active);
            var session = await active.SwitchAsync(binding.Scope, stoppingToken);
            logger.LogInformation("Deribit read-only capture starting; coherent recovery remains required.");
            await stream.RunCaptureAsync(active, session, binding.Plan, stoppingToken);
            if (!stoppingToken.IsCancellationRequested) throw new InvalidOperationException();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            // No DB details, private account data, auth frames or upstream error messages reach host logs.
            throw new InvalidOperationException("Deribit read-only capture stopped; recovery and operator review required.");
        }
    }
}
