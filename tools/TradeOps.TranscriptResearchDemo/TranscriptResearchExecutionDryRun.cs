using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;

namespace TradeOps.TranscriptResearchDemo;

public sealed record TranscriptResearchExecutionDryRunOutput(
    string Mode,
    string State,
    Guid SignalId,
    string DecisionId,
    string? ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderType OrderType,
    decimal Quantity,
    decimal ReferencePrice,
    decimal EstimatedNotional,
    bool RiskAllowed,
    bool MutationPerformed,
    bool BrokerRequestSent,
    bool PersistencePerformed);

public sealed class TranscriptResearchExecutionDryRunService(
    IClientOrderIdGenerator clientOrderIdGenerator)
{
    public const string PreparedState =
        "Prepared";

    public const string BlockedByRiskState =
        "BlockedByRisk";

    public TranscriptResearchExecutionDryRunOutput Run(
        RebalancePlan plan,
        TranscriptResearchRiskPreviewExecution riskExecution)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(riskExecution);

        var intent =
            plan.OrderIntent
            ?? throw new InvalidOperationException(
                "CurrentRebalancePlan.OrderIntent is required for execution dry-run.");
        var signal =
            riskExecution.Signal;
        var risk =
            riskExecution.Output;

        EnsureSameIntent(
            intent,
            signal,
            risk);

        var clientOrderId =
            risk.Allowed
                ? clientOrderIdGenerator.Generate(
                    signal.Id)
                : null;

        return new TranscriptResearchExecutionDryRunOutput(
            "dryRun",
            risk.Allowed
                ? PreparedState
                : BlockedByRiskState,
            signal.Id,
            intent.DecisionId,
            clientOrderId,
            intent.Instrument.Symbol,
            intent.Side,
            OrderType.Market,
            intent.Quantity,
            intent.ReferencePrice,
            intent.EstimatedNotional,
            risk.Allowed,
            MutationPerformed:
                false,
            BrokerRequestSent:
                false,
            PersistencePerformed:
                false);
    }

    private static void EnsureSameIntent(
        RebalanceOrderIntent intent,
        TradeOps.Domain.Entities.TradingSignal signal,
        TranscriptResearchRiskPreviewOutput risk)
    {
        if (risk.SignalId != signal.Id
            || !string.Equals(
                risk.DecisionId,
                intent.DecisionId,
                StringComparison.Ordinal)
            || !string.Equals(
                signal.Symbol,
                intent.Instrument.Symbol,
                StringComparison.Ordinal)
            || signal.Side != intent.Side
            || signal.RequestedQuantity != intent.Quantity)
        {
            throw new InvalidOperationException(
                "Risk preview execution identity does not match CurrentRebalancePlan.OrderIntent.");
        }
    }
}

public sealed record TranscriptResearchExecutionDryRunDemoOutput(
    int SchemaVersion,
    TranscriptResearchRebalanceInputProvenanceOutput InputProvenance,
    TranscriptResearchDemoPolicyOutput Policy,
    TranscriptResearchDemoInstrumentOutput Instrument,
    TranscriptResearchDemoArtifactOutput Prior,
    TranscriptResearchDemoArtifactOutput Current,
    TranscriptResearchDemoAssessmentOutput Assessment,
    ResearchDecision ResearchDecision,
    DateTimeOffset BacktestPeriodStart,
    DateTimeOffset BacktestPeriodEnd,
    BacktestPerformanceMetrics BacktestMetrics,
    PortfolioSnapshot FinalBacktestPortfolio,
    RebalancePlan CurrentRebalancePlan,
    TranscriptResearchRiskPreviewOutput RiskPreview,
    TranscriptResearchExecutionDryRunOutput ExecutionDryRun);

public static class TranscriptResearchExecutionDryRunDemoRunner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

    public static int Run(
        TranscriptResearchDemoResolvedManifest manifest,
        EarningsResearchPolicyDefinition policy,
        string policyFingerprint,
        string priorNormalized,
        string priorStructured,
        string currentNormalized,
        string currentStructured,
        string rebalanceInputPath,
        string riskPreviewInputPath,
        string? jsonPath,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);

        var riskExecution =
            TranscriptResearchRiskPreviewDemoRunner
                .Create(
                    manifest,
                    policy,
                    policyFingerprint,
                    priorNormalized,
                    priorStructured,
                    currentNormalized,
                    currentStructured,
                    rebalanceInputPath,
                    riskPreviewInputPath);
        var baseline =
            riskExecution.Artifact;
        var dryRun =
            new TranscriptResearchExecutionDryRunService(
                    new ClientOrderIdGenerator())
                .Run(
                    baseline.CurrentRebalancePlan,
                    riskExecution.RiskExecution);
        var artifact =
            new TranscriptResearchExecutionDryRunDemoOutput(
                baseline.SchemaVersion,
                baseline.InputProvenance,
                baseline.Policy,
                baseline.Instrument,
                baseline.Prior,
                baseline.Current,
                baseline.Assessment,
                baseline.ResearchDecision,
                baseline.BacktestPeriodStart,
                baseline.BacktestPeriodEnd,
                baseline.BacktestMetrics,
                baseline.FinalBacktestPortfolio,
                baseline.CurrentRebalancePlan,
                baseline.RiskPreview,
                dryRun);
        var outputPath =
            ResolveJsonOutputPath(
                jsonPath,
                manifest,
                riskPreviewInputPath);
        var directory =
            Path.GetDirectoryName(
                outputPath);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(
                artifact,
                JsonOptions));

        output.WriteLine(
            "TRANSCRIPT RESEARCH EXECUTION DRY-RUN DEMO: PASS");
        output.WriteLine(
            "Instrument: " +
            artifact.Instrument.Symbol);
        output.WriteLine(
            "Risk allowed: " +
            artifact.RiskPreview.Allowed);
        output.WriteLine(
            "Execution dry-run state: " +
            artifact.ExecutionDryRun.State);
        output.WriteLine(
            "Client order id: " +
            (artifact.ExecutionDryRun.ClientOrderId
             ?? "n/a"));
        output.WriteLine(
            "JSON artifact: " +
            outputPath);

        return 0;
    }

    private static string ResolveJsonOutputPath(
        string? jsonPath,
        TranscriptResearchDemoResolvedManifest manifest,
        string riskPreviewInputPath)
    {
        if (!string.IsNullOrWhiteSpace(
                jsonPath))
        {
            return Path.GetFullPath(
                jsonPath);
        }

        var directory =
            Path.GetDirectoryName(
                manifest.Prior.NormalizedDocumentPath)
            ?? Path.GetDirectoryName(
                Path.GetFullPath(
                    riskPreviewInputPath));

        return Path.Combine(
            directory
            ?? throw new InvalidDataException(
                "VS-17 output directory could not be resolved."),
            "transcript-research-execution-dry-run-result.json");
    }
}
