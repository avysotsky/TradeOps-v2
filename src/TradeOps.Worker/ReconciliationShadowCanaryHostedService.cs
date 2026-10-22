using TradeOps.Infrastructure.Exchange;

namespace TradeOps.Worker;

/// <summary>
/// An opt-in ONE-SHOT synthetic canary, never production reconciliation.
/// Real persisted orders currently have no broker/account/instrument scope,
/// so routing them to the new decision engine would be unsafe.
/// </summary>
public sealed class ReconciliationShadowStartupPolicy
{
    public const string SectionName = "ReconciliationShadow";
    public const string EnvironmentConfirm = "TRADEOPS_RECON_SHADOW_CONFIRM";
    public const string RequiredConfirm = "SYNTHETIC_MOCK_IBKR_ONLY";

    public bool Enabled { get; set; }

    public static void DemandSyntheticOnly(
        bool enabled,
        string? exchangeProvider,
        string? confirmation)
    {
        if (!enabled)
        {
            return;
        }

        // Explicit production fail-closed gate. No other exchange or
        // IBKR Paper environment may start this optional HostedService.
        if (!string.Equals(exchangeProvider, "Mock", StringComparison.Ordinal)
            || !string.Equals(confirmation, RequiredConfirm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shadow canary requires Mock exchange and explicit synthetic-only confirmation.");
        }
    }
}

/// <summary>
/// Does not request a DI scope, repositories, EF Core, IExchangeClient,
/// HTTP clients, alerts or order/position services. Failure prevents a
/// mistakenly enabled runtime canary from being reported as healthy.
/// </summary>
public sealed class ReconciliationShadowCanaryHostedService(
    TradeOps.Application.Interfaces.IBrokerReconciliationDecisionEngine engine,
    ILogger<ReconciliationShadowCanaryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await OfflineReconciliationShadowCanary.RunAsync(
            engine, cancellationToken);

        // Fixed summary-only marker, deliberately no account IDs, order IDs,
        // instrument details, secrets, broker payloads or connection strings.
        logger.LogInformation(
            "TRADEOPS SHADOW RECONCILIATION CANARY PASS providers={Providers} evaluations={Evaluations} candidates={Candidates} unresolved={Unresolved} db_writes={DatabaseWrites} broker_requests={BrokerRequests} mode=synthetic_only",
            result.Providers,
            result.Evaluations,
            result.Candidates,
            result.Unresolved,
            result.DatabaseWrites,
            result.BrokerRequests);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
