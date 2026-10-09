using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.TranscriptResearchDemo;

public sealed record TranscriptResearchRiskPreviewInput(
    RiskSettings Settings,
    RiskControlSnapshot ControlSnapshot);

public static class TranscriptResearchRiskPreviewInputLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] RequiredProperties =
    {
        "schemaVersion",
        "maxPositionSize",
        "maxOrderSize",
        "maxDailyLoss",
        "maxOpenPositions",
        "allowedSymbols",
        "tradingEnabled",
        "emergencyStop",
        "emergencyStopReason",
        "settlementCurrency",
        "dailyNetRealizedPnL",
        "activePositionMismatchCount",
        "updatedAt"
    };

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public static TranscriptResearchRiskPreviewInput Load(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid(
                "Risk preview input path is required.");
        }

        var fullPath =
            Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Risk preview input file was not found.",
                fullPath);
        }

        var json =
            File.ReadAllText(fullPath);
        RiskPreviewInputDto? dto;

        try
        {
            using var document =
                JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling =
                            JsonCommentHandling.Disallow
                    });

            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                throw Invalid(
                    "Risk preview input JSON must contain an object.");
            }

            foreach (var property in
                     RequiredProperties)
            {
                if (!document.RootElement.TryGetProperty(
                        property,
                        out _))
                {
                    throw Invalid(
                        $"Risk preview input requires '{property}'.");
                }
            }

            dto =
                JsonSerializer.Deserialize<RiskPreviewInputDto>(
                    json,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Risk preview input JSON does not match the strict VS-16 schema.",
                exception);
        }

        if (dto is null)
        {
            throw Invalid(
                "Risk preview input JSON must contain an object.");
        }

        if (dto.SchemaVersion !=
            SupportedSchemaVersion)
        {
            throw Invalid(
                $"schemaVersion must be exactly {SupportedSchemaVersion}.");
        }

        if (!dto.MaxPositionSize.HasValue
            || dto.MaxPositionSize.Value <= 0m)
        {
            throw Invalid(
                "maxPositionSize must be greater than zero.");
        }

        if (!dto.MaxOrderSize.HasValue
            || dto.MaxOrderSize.Value <= 0m)
        {
            throw Invalid(
                "maxOrderSize must be greater than zero.");
        }

        if (!dto.MaxDailyLoss.HasValue
            || dto.MaxDailyLoss.Value <= 0m)
        {
            throw Invalid(
                "maxDailyLoss must be greater than zero.");
        }

        if (!dto.MaxOpenPositions.HasValue
            || dto.MaxOpenPositions.Value <= 0)
        {
            throw Invalid(
                "maxOpenPositions must be greater than zero.");
        }

        if (dto.AllowedSymbols is null
            || dto.AllowedSymbols.Count == 0)
        {
            throw Invalid(
                "allowedSymbols must contain at least one symbol.");
        }

        var allowedSymbols =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in
                 dto.AllowedSymbols)
        {
            if (string.IsNullOrWhiteSpace(
                    symbol))
            {
                throw Invalid(
                    "allowedSymbols must not contain blank symbols.");
            }

            if (!allowedSymbols.Add(
                    symbol))
            {
                throw Invalid(
                    $"allowedSymbols contains duplicate symbol '{symbol}'.");
            }
        }

        if (!dto.TradingEnabled.HasValue)
        {
            throw Invalid(
                "tradingEnabled is required.");
        }

        if (!dto.EmergencyStop.HasValue)
        {
            throw Invalid(
                "emergencyStop is required.");
        }

        if (string.IsNullOrWhiteSpace(
                dto.SettlementCurrency))
        {
            throw Invalid(
                "settlementCurrency is required.");
        }

        if (!dto.ActivePositionMismatchCount.HasValue
            || dto.ActivePositionMismatchCount.Value < 0)
        {
            throw Invalid(
                "activePositionMismatchCount must be zero or greater.");
        }

        if (!dto.UpdatedAt.HasValue)
        {
            throw Invalid(
                "updatedAt is required.");
        }

        var settings =
            new RiskSettings
            {
                MaxPositionSize =
                    dto.MaxPositionSize.Value,
                MaxOrderSize =
                    dto.MaxOrderSize.Value,
                MaxDailyLoss =
                    dto.MaxDailyLoss.Value,
                MaxOpenPositions =
                    dto.MaxOpenPositions.Value,
                AllowedSymbols =
                    allowedSymbols,
                TradingEnabled =
                    dto.TradingEnabled.Value,
                EmergencyStop =
                    dto.EmergencyStop.Value
            };

        var control =
            new RiskControlSnapshot(
                dto.TradingEnabled.Value,
                dto.EmergencyStop.Value,
                dto.EmergencyStopReason,
                dto.SettlementCurrency,
                dto.DailyNetRealizedPnL ?? 0m,
                0m,
                dto.DailyNetRealizedPnL,
                Array.Empty<UnconvertedFee>(),
                dto.ActivePositionMismatchCount.Value,
                dto.UpdatedAt.Value);

        return new TranscriptResearchRiskPreviewInput(
            settings,
            control);
    }

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private sealed class RiskPreviewInputDto
    {
        public int? SchemaVersion { get; init; }
        public decimal? MaxPositionSize { get; init; }
        public decimal? MaxOrderSize { get; init; }
        public decimal? MaxDailyLoss { get; init; }
        public int? MaxOpenPositions { get; init; }
        public List<string?>? AllowedSymbols { get; init; }
        public bool? TradingEnabled { get; init; }
        public bool? EmergencyStop { get; init; }
        public string? EmergencyStopReason { get; init; }
        public string? SettlementCurrency { get; init; }
        public decimal? DailyNetRealizedPnL { get; init; }
        public int? ActivePositionMismatchCount { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}

public static class RebalanceRiskPreviewSignalProjector
{
    public const string PreviewSignalType =
        "RebalanceRiskPreview";

    public const string PreviewSignalSource =
        "transcript-research-rebalance-risk-preview";

    public static TradingSignal Project(RebalancePlan plan)
        => RebalancePreviewSignalProjector.Project(plan);

}

public sealed class TranscriptResearchRiskPreviewExchangeClient :
    IExchangeClient
{
    private readonly IReadOnlyCollection<Position> _positions;

    public TranscriptResearchRiskPreviewExchangeClient(
        PortfolioSnapshot currentPortfolio)
    {
        ArgumentNullException.ThrowIfNull(
            currentPortfolio);

        _positions =
            currentPortfolio.Positions
                .Where(
                    item =>
                        item.Quantity != 0m)
                .Select(
                    item =>
                        new Position
                        {
                            Symbol =
                                item.Instrument.Symbol,
                            Side =
                                item.Quantity > 0m
                                    ? OrderSide.Buy
                                    : OrderSide.Sell,
                            Quantity =
                                Math.Abs(
                                    item.Quantity),
                            AverageEntryPrice = 0m,
                            MarkPrice = 0m,
                            UnrealizedPnL = 0m
                        })
                .ToArray();
    }

    public int GetPositionsCallCount
    {
        get;
        private set;
    }

    public int MutationAttemptCount
    {
        get;
        private set;
    }

    public IReadOnlyCollection<Position> Positions =>
        _positions;

    public Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Risk preview exposes current positions only.");

    public Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        GetPositionsCallCount++;

        return Task.FromResult(
            _positions);
    }

    public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Risk preview does not expose open orders.");

    public Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        MutationAttemptCount++;

        throw new InvalidOperationException(
            "Order placement is prohibited in VS-16 risk preview.");
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        MutationAttemptCount++;

        throw new InvalidOperationException(
            "Order cancellation is prohibited in VS-16 risk preview.");
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Risk preview does not expose order state.");

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Risk preview does not expose order state.");
}

public sealed class TranscriptResearchRiskPreviewControlService :
    IRiskControlService
{
    private readonly RiskControlSnapshot _snapshot;

    public TranscriptResearchRiskPreviewControlService(
        RiskControlSnapshot snapshot)
    {
        _snapshot =
            snapshot
            ?? throw new ArgumentNullException(
                nameof(snapshot));
    }

    public int SnapshotReadCount
    {
        get;
        private set;
    }

    public int StateMutationAttemptCount
    {
        get;
        private set;
    }

    public List<RiskDecision> RecordedRejections
    {
        get;
    } =
        new();

    public Task<RiskControlSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        SnapshotReadCount++;

        return Task.FromResult(
            _snapshot);
    }

    public Task<RiskControlSnapshot> SetTradingEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        StateMutationAttemptCount++;

        throw new InvalidOperationException(
            "Operational risk state mutation is prohibited in VS-16 risk preview.");
    }

    public Task<RiskControlSnapshot> SetEmergencyStopAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        StateMutationAttemptCount++;

        throw new InvalidOperationException(
            "Operational risk state mutation is prohibited in VS-16 risk preview.");
    }

    public Task RecordRejectionAsync(
        TradingSignal signal,
        RiskDecision decision,
        CancellationToken cancellationToken = default)
    {
        RecordedRejections.Add(
            decision);

        return Task.CompletedTask;
    }
}

public sealed record TranscriptResearchRiskPreviewOutput(
    string Mode,
    Guid SignalId,
    string DecisionId,
    string Symbol,
    OrderSide Side,
    decimal RequestedQuantity,
    bool RequiresRiskApproval,
    bool Allowed,
    IReadOnlyCollection<string> Reasons);

public sealed record TranscriptResearchRiskPreviewExecution(
    TranscriptResearchRiskPreviewOutput Output,
    TradingSignal Signal,
    IReadOnlyCollection<Position> ExchangePositions,
    int RiskEngineCheckCount,
    int RiskSnapshotReadCount,
    int ExchangePositionReadCount,
    int ExchangeMutationAttemptCount,
    int RiskStateMutationAttemptCount,
    int RecordedRejectionCount);

public sealed class TranscriptResearchRiskPreviewService
{
    public TranscriptResearchRiskPreviewExecution Run(
        RebalancePlan plan,
        PortfolioSnapshot currentPortfolio,
        TranscriptResearchRiskPreviewInput input)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentPortfolio);
        ArgumentNullException.ThrowIfNull(input);

        var signal =
            RebalanceRiskPreviewSignalProjector
                .Project(
                    plan);
        var exchange =
            new TranscriptResearchRiskPreviewExchangeClient(
                currentPortfolio);
        var control =
            new TranscriptResearchRiskPreviewControlService(
                input.ControlSnapshot);
        var riskEngine =
            new RiskEngine(
                exchange,
                control,
                input.Settings);
        var riskEngineCheckCount =
            0;

        riskEngineCheckCount++;
        var decision =
            riskEngine
                .CheckAsync(
                    signal)
                .GetAwaiter()
                .GetResult();

        if (exchange.MutationAttemptCount != 0
            || control.StateMutationAttemptCount != 0)
        {
            throw new InvalidOperationException(
                "VS-16 risk preview attempted a forbidden mutation.");
        }

        var intent =
            plan.OrderIntent!;
        var preview =
            new TranscriptResearchRiskPreviewOutput(
                "deterministicSyntheticState",
                signal.Id,
                intent.DecisionId,
                signal.Symbol,
                signal.Side,
                signal.RequestedQuantity,
                intent.RequiresRiskApproval,
                decision.IsAllowed,
                decision.Reasons.ToArray());

        return new TranscriptResearchRiskPreviewExecution(
            preview,
            signal,
            exchange.Positions,
            riskEngineCheckCount,
            control.SnapshotReadCount,
            exchange.GetPositionsCallCount,
            exchange.MutationAttemptCount,
            control.StateMutationAttemptCount,
            control.RecordedRejections.Count);
    }
}

public sealed record TranscriptResearchRiskPreviewDemoOutput(
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
    TranscriptResearchRiskPreviewOutput RiskPreview);

public sealed record TranscriptResearchRiskPreviewDemoExecution(
    TranscriptResearchRiskPreviewDemoOutput Artifact,
    TranscriptResearchRiskPreviewExecution RiskExecution);

public static class TranscriptResearchRiskPreviewDemoRunner
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

        var execution =
            Create(
                manifest,
                policy,
                policyFingerprint,
                priorNormalized,
                priorStructured,
                currentNormalized,
                currentStructured,
                rebalanceInputPath,
                riskPreviewInputPath);
        var artifact =
            execution.Artifact;
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
            "TRANSCRIPT RESEARCH RISK PREVIEW DEMO: PASS");
        output.WriteLine(
            "Instrument: " +
            artifact.Instrument.Symbol);
        output.WriteLine(
            "Rebalance status: " +
            artifact.CurrentRebalancePlan.Status);
        output.WriteLine(
            "Risk allowed: " +
            artifact.RiskPreview.Allowed);
        output.WriteLine(
            "Risk signal: " +
            artifact.RiskPreview.SignalId);
        output.WriteLine(
            "Policy fingerprint: " +
            artifact.Policy.Fingerprint);
        output.WriteLine(
            "JSON artifact: " +
            outputPath);

        return 0;
    }


    public static TranscriptResearchRiskPreviewDemoExecution Create(
        TranscriptResearchDemoResolvedManifest manifest,
        EarningsResearchPolicyDefinition policy,
        string policyFingerprint,
        string priorNormalized,
        string priorStructured,
        string currentNormalized,
        string currentStructured,
        string rebalanceInputPath,
        string riskPreviewInputPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);

        var riskInput =
            TranscriptResearchRiskPreviewInputLoader
                .Load(
                    riskPreviewInputPath);
        var rebalance =
            TranscriptResearchRebalanceDemoRunner
                .Create(
                    manifest,
                    policy,
                    policyFingerprint,
                    priorNormalized,
                    priorStructured,
                    currentNormalized,
                    currentStructured,
                    rebalanceInputPath);
        var risk =
            new TranscriptResearchRiskPreviewService()
                .Run(
                    rebalance.Artifact
                        .CurrentRebalancePlan,
                    rebalance.Input
                        .CurrentPortfolio,
                    riskInput);
        var baseline =
            rebalance.Artifact;
        var artifact =
            new TranscriptResearchRiskPreviewDemoOutput(
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
                risk.Output);

        return new TranscriptResearchRiskPreviewDemoExecution(
            artifact,
            risk);
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
                "VS-16 output directory could not be resolved."),
            "transcript-research-risk-preview-result.json");
    }
}
