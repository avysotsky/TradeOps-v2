using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.TranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TranscriptResearchExecutionDryRunTests
{
    [Fact]
    public void Allowed_preview_prepares_deterministic_identity_with_production_client_order_id()
    {
        var plan =
            CreatePlan();
        var risk =
            new TranscriptResearchRiskPreviewService()
                .Run(
                    plan,
                    CreatePortfolio(),
                    CreateRiskInput());

        var service =
            new TranscriptResearchExecutionDryRunService(
                new ClientOrderIdGenerator());

        var first =
            service.Run(
                plan,
                risk);
        var second =
            service.Run(
                plan,
                risk);
        var expectedClientOrderId =
            new ClientOrderIdGenerator()
                .Generate(
                    risk.Signal.Id);

        Assert.Equal(
            TranscriptResearchExecutionDryRunService.PreparedState,
            first.State);
        Assert.Equal(
            "dryRun",
            first.Mode);
        Assert.Equal(
            risk.Signal.Id,
            first.SignalId);
        Assert.Equal(
            expectedClientOrderId,
            first.ClientOrderId);
        Assert.Equal(
            first.ClientOrderId,
            second.ClientOrderId);
        Assert.Equal(
            plan.OrderIntent!.DecisionId,
            first.DecisionId);
        Assert.Equal(
            plan.OrderIntent.Instrument.Symbol,
            first.Symbol);
        Assert.Equal(
            plan.OrderIntent.Side,
            first.Side);
        Assert.Equal(
            plan.OrderIntent.Quantity,
            first.Quantity);
        Assert.Equal(
            plan.OrderIntent.ReferencePrice,
            first.ReferencePrice);
        Assert.Equal(
            plan.OrderIntent.EstimatedNotional,
            first.EstimatedNotional);
        Assert.Equal(
            OrderType.Market,
            first.OrderType);
        Assert.True(
            first.RiskAllowed);
        Assert.False(
            first.MutationPerformed);
        Assert.False(
            first.BrokerRequestSent);
        Assert.False(
            first.PersistencePerformed);
    }

    [Fact]
    public void Rejected_preview_is_blocked_without_prepared_client_order_identity()
    {
        var plan =
            CreatePlan();
        var risk =
            new TranscriptResearchRiskPreviewService()
                .Run(
                    plan,
                    CreatePortfolio(),
                    CreateRiskInput(
                        tradingEnabled:
                            false));
        var result =
            new TranscriptResearchExecutionDryRunService(
                    new ClientOrderIdGenerator())
                .Run(
                    plan,
                    risk);

        Assert.Equal(
            TranscriptResearchExecutionDryRunService.BlockedByRiskState,
            result.State);
        Assert.False(
            result.RiskAllowed);
        Assert.Null(
            result.ClientOrderId);
        Assert.False(
            result.MutationPerformed);
        Assert.False(
            result.BrokerRequestSent);
        Assert.False(
            result.PersistencePerformed);
    }

    [Fact]
    public void Cli_execution_dry_run_requires_risk_preview_input()
    {
        using var output =
            new StringWriter();
        using var error =
            new StringWriter();

        var exitCode =
            TranscriptResearchDemoCli.Run(
                new[]
                {
                    "--manifest",
                    "manifest.json",
                    "--policy",
                    "policy.json",
                    "--rebalance-input",
                    "rebalance.json",
                    "--execution-dry-run"
                },
                output,
                error);

        Assert.Equal(
            1,
            exitCode);
        Assert.Contains(
            "--execution-dry-run requires --risk-preview-input",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_dry_run_cli_preserves_research_rebalance_and_risk_evidence()
    {
        var root =
            FindRepositoryRoot();
        var temp =
            Path.Combine(
                Path.GetTempPath(),
                "tradeops-vs17-" +
                Guid.NewGuid()
                    .ToString("N"));
        Directory.CreateDirectory(
            temp);

        try
        {
            var outputPath =
                Path.Combine(
                    temp,
                    "execution-dry-run-result.json");
            using var output =
                new StringWriter();
            using var error =
                new StringWriter();

            var exitCode =
                TranscriptResearchDemoCli.Run(
                    new[]
                    {
                        "--manifest",
                        Path.Combine(
                            root,
                            "samples",
                            "research",
                            "transcript-research",
                            "manifest.json"),
                        "--policy",
                        Path.Combine(
                            root,
                            "samples",
                            "research",
                            "transcript-research",
                            "policy.json"),
                        "--rebalance-input",
                        Path.Combine(
                            root,
                            "samples",
                            "research",
                            "provider-transcript-demo",
                            "rebalance-input.json"),
                        "--risk-preview-input",
                        Path.Combine(
                            root,
                            "samples",
                            "research",
                            "provider-transcript-demo",
                            "risk-preview-input.json"),
                        "--execution-dry-run",
                        "--json",
                        outputPath
                    },
                    output,
                    error);

            Assert.Equal(
                0,
                exitCode);

            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(
                        outputPath));
            var rootElement =
                document.RootElement;
            var intent =
                rootElement
                    .GetProperty(
                        "currentRebalancePlan")
                    .GetProperty(
                        "orderIntent");
            var risk =
                rootElement.GetProperty(
                    "riskPreview");
            var dryRun =
                rootElement.GetProperty(
                    "executionDryRun");

            Assert.Equal(
                0.40m,
                rootElement
                    .GetProperty(
                        "researchDecision")
                    .GetProperty(
                        "targetWeight")
                    .GetDecimal());
            Assert.Equal(
                "SAMP",
                intent.GetProperty(
                        "instrument")
                    .GetProperty(
                        "symbol")
                    .GetString());
            Assert.True(
                risk.GetProperty(
                        "allowed")
                    .GetBoolean());
            Assert.Equal(
                "Prepared",
                dryRun.GetProperty(
                        "state")
                    .GetString());
            Assert.Equal(
                risk.GetProperty(
                        "signalId")
                    .GetGuid(),
                dryRun.GetProperty(
                        "signalId")
                    .GetGuid());
            Assert.Equal(
                intent.GetProperty(
                        "quantity")
                    .GetDecimal(),
                dryRun.GetProperty(
                        "quantity")
                    .GetDecimal());
            Assert.Equal(
                "Market",
                dryRun.GetProperty(
                        "orderType")
                    .GetString());
            Assert.StartsWith(
                "trd-",
                dryRun.GetProperty(
                        "clientOrderId")
                    .GetString(),
                StringComparison.Ordinal);
            Assert.False(
                dryRun.GetProperty(
                        "mutationPerformed")
                    .GetBoolean());
            Assert.False(
                dryRun.GetProperty(
                        "brokerRequestSent")
                    .GetBoolean());
            Assert.False(
                dryRun.GetProperty(
                        "persistencePerformed")
                    .GetBoolean());

            var json =
                File.ReadAllText(
                    outputPath);
            Assert.DoesNotContain(
                "apiKey",
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "GROQ_API_KEY",
                json,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(
                temp,
                recursive:
                    true);
        }
    }

    [Fact]
    public void Owned_dry_run_code_has_no_persistence_exchange_or_execution_service_dependency()
    {
        var source =
            File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "tools",
                    "TradeOps.TranscriptResearchDemo",
                    "TranscriptResearchExecutionDryRun.cs"));

        Assert.Contains(
            "new ClientOrderIdGenerator()",
            source,
            StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "OrderManager",
                     "SignalExecutionService",
                     "IOrderRepository",
                     "ITradingSignalRepository",
                     "IOrderStateMachine",
                     "IAlertService",
                     "IExchangeClient",
                     "PlaceOrderAsync",
                     "CancelOrderAsync",
                     "IbkrPaperExchangeClient",
                     "new Order"
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                source,
                StringComparison.Ordinal);
        }
    }

    private static RebalancePlan CreatePlan()
    {
        var instrument =
            CreateInstrument();
        var plannedAt =
            new DateTimeOffset(
                2026,
                7,
                24,
                22,
                0,
                0,
                TimeSpan.Zero);
        var intent =
            new RebalanceOrderIntent(
                "decision-samp-risk-preview",
                instrument,
                OrderSide.Buy,
                30m,
                100m,
                3000m);

        return new RebalancePlan(
            intent.DecisionId,
            "sample-strategy",
            plannedAt,
            new TargetPosition(
                instrument,
                0.40m,
                100m,
                4000m,
                40m),
            10m,
            1000m,
            30m,
            3000m,
            RebalancePlanStatus.Ready,
            intent,
            Array.Empty<RebalanceConstraintViolation>());
    }

    private static PortfolioSnapshot CreatePortfolio()
    {
        var instrument =
            CreateInstrument();

        return new PortfolioSnapshot(
            "USD",
            10000m,
            9000m,
            new[]
            {
                new PortfolioPosition(
                    instrument,
                    10m)
            },
            new DateTimeOffset(
                2026,
                7,
                24,
                22,
                0,
                0,
                TimeSpan.Zero));
    }

    private static InstrumentReference CreateInstrument() =>
        new(
            "SAMP",
            AssetClass.Stock,
            "USD",
            null,
            "XNYS");

    private static TranscriptResearchRiskPreviewInput CreateRiskInput(
        bool tradingEnabled = true)
    {
        var settings =
            new RiskSettings
            {
                MaxPositionSize =
                    50m,
                MaxOrderSize =
                    50m,
                MaxDailyLoss =
                    1000m,
                MaxOpenPositions =
                    5,
                AllowedSymbols =
                    new HashSet<string>(
                        new[]
                        {
                            "SAMP"
                        },
                        StringComparer.OrdinalIgnoreCase),
                TradingEnabled =
                    tradingEnabled
            };
        var snapshot =
            new RiskControlSnapshot(
                tradingEnabled,
                false,
                null,
                "USD",
                0m,
                0m,
                0m,
                Array.Empty<UnconvertedFee>(),
                0,
                new DateTimeOffset(
                    2026,
                    7,
                    24,
                    22,
                    0,
                    0,
                    TimeSpan.Zero));

        return new TranscriptResearchRiskPreviewInput(
            settings,
            snapshot);
    }

    private static string FindRepositoryRoot()
    {
        var directory =
            new DirectoryInfo(
                AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "TradeOps.sln")))
            {
                return directory.FullName;
            }

            directory =
                directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "TradeOps repository root not found.");
    }
}
