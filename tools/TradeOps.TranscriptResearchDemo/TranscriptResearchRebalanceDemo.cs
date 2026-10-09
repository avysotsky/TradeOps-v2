using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;
using TradeOps.Application.Services;

namespace TradeOps.TranscriptResearchDemo;

public sealed record TranscriptResearchRebalanceInputProvenanceOutput(
    string FileName,
    string ContentFingerprint);

public sealed record TranscriptResearchRebalanceDemoOutput(
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
    RebalancePlan CurrentRebalancePlan);

public sealed record TranscriptResearchRebalanceInput(
    IReadOnlyList<MarketDataBar> HistoricalDailyMarketBars,
    decimal InitialCash,
    PortfolioSnapshot CurrentPortfolio,
    decimal CurrentReferencePrice,
    BacktestExecutionCosts? ExecutionCosts,
    RebalanceConstraints? BacktestConstraints,
    RebalanceConstraints? CurrentRebalanceConstraints,
    TranscriptResearchRebalanceInputProvenanceOutput Provenance);

public static class TranscriptResearchRebalanceInputLoader
{
    public const int SupportedSchemaVersion = 1;

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

    public static TranscriptResearchRebalanceInput Load(
        string path,
        InstrumentReference manifestInstrument)
    {
        ArgumentNullException.ThrowIfNull(
            manifestInstrument);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid(
                "Rebalance input path is required.");
        }

        var fullPath =
            Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Rebalance input file was not found.",
                fullPath);
        }

        var json =
            File.ReadAllText(fullPath);
        RebalanceInputDto? dto;

        try
        {
            dto =
                JsonSerializer.Deserialize<RebalanceInputDto>(
                    json,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Rebalance input JSON does not match the strict VS-13 demo schema.",
                exception);
        }

        if (dto is null)
        {
            throw Invalid(
                "Rebalance input JSON must contain an object.");
        }

        if (dto.SchemaVersion !=
            SupportedSchemaVersion)
        {
            throw Invalid(
                $"schemaVersion must be exactly {SupportedSchemaVersion}.");
        }

        ValidateInstrument(
            dto.Instrument,
            manifestInstrument);

        if (dto.HistoricalDailyMarketBars is null)
        {
            throw Invalid(
                "historicalDailyMarketBars is required.");
        }

        if (!dto.InitialCash.HasValue)
        {
            throw Invalid(
                "initialCash is required.");
        }

        if (dto.CurrentPortfolio is null)
        {
            throw Invalid(
                "currentPortfolio is required.");
        }

        if (!dto.CurrentReferencePrice.HasValue)
        {
            throw Invalid(
                "currentReferencePrice is required.");
        }

        var bars =
            dto.HistoricalDailyMarketBars
                .Select(
                    item =>
                        ToBar(
                            item,
                            manifestInstrument))
                .ToArray();
        var portfolio =
            ToPortfolio(
                dto.CurrentPortfolio,
                manifestInstrument);

        return new TranscriptResearchRebalanceInput(
            bars,
            dto.InitialCash.Value,
            portfolio,
            dto.CurrentReferencePrice.Value,
            ToExecutionCosts(
                dto.ExecutionCosts),
            ToConstraints(
                dto.BacktestConstraints),
            ToConstraints(
                dto.CurrentRebalanceConstraints),
            new TranscriptResearchRebalanceInputProvenanceOutput(
                Path.GetFileName(fullPath),
                ComputeFingerprint(json)));
    }

    private static void ValidateInstrument(
        InstrumentDto? input,
        InstrumentReference expected)
    {
        if (input is null)
        {
            throw Invalid(
                "instrument is required.");
        }

        if (!string.Equals(
                input.Symbol,
                expected.Symbol,
                StringComparison.Ordinal)
            || !string.Equals(
                input.AssetClass,
                expected.AssetClass.ToString(),
                StringComparison.Ordinal)
            || !string.Equals(
                input.Currency,
                expected.Currency,
                StringComparison.Ordinal)
            || !string.Equals(
                input.VenueInstrumentId,
                expected.VenueInstrumentId,
                StringComparison.Ordinal)
            || !string.Equals(
                input.Exchange,
                expected.Exchange,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "Rebalance input instrument must exactly match the transcript manifest instrument.");
        }
    }

    private static MarketDataBar ToBar(
        MarketDataBarDto dto,
        InstrumentReference instrument)
    {
        if (!dto.OpenTime.HasValue
            || !dto.CloseTime.HasValue
            || !dto.Open.HasValue
            || !dto.High.HasValue
            || !dto.Low.HasValue
            || !dto.Close.HasValue)
        {
            throw Invalid(
                "Each historicalDailyMarketBars item requires openTime, closeTime, open, high, low, and close.");
        }

        return new MarketDataBar(
            instrument,
            MarketDataBarPeriod.Daily,
            dto.OpenTime.Value,
            dto.CloseTime.Value,
            dto.Open.Value,
            dto.High.Value,
            dto.Low.Value,
            dto.Close.Value);
    }

    private static PortfolioSnapshot ToPortfolio(
        PortfolioDto dto,
        InstrumentReference instrument)
    {
        if (string.IsNullOrWhiteSpace(
                dto.BaseCurrency)
            || !dto.NetAssetValue.HasValue
            || !dto.Cash.HasValue
            || dto.Positions is null
            || !dto.AsOf.HasValue)
        {
            throw Invalid(
                "currentPortfolio requires baseCurrency, netAssetValue, cash, positions, and asOf.");
        }

        var positions =
            dto.Positions
                .Select(
                    item =>
                    {
                        if (!item.Quantity.HasValue)
                        {
                            throw Invalid(
                                "Each currentPortfolio position requires quantity.");
                        }

                        return new PortfolioPosition(
                            instrument,
                            item.Quantity.Value);
                    })
                .ToArray();

        return new PortfolioSnapshot(
            dto.BaseCurrency,
            dto.NetAssetValue.Value,
            dto.Cash.Value,
            positions,
            dto.AsOf.Value);
    }

    private static BacktestExecutionCosts? ToExecutionCosts(
        ExecutionCostsDto? dto) =>
        dto is null
            ? null
            : new BacktestExecutionCosts(
                dto.CommissionPerOrder ?? 0m,
                dto.SlippageBasisPoints ?? 0m);

    private static RebalanceConstraints? ToConstraints(
        ConstraintsDto? dto) =>
        dto is null
            ? null
            : new RebalanceConstraints(
                dto.MaxTargetWeight ?? 1m,
                dto.MinimumTradeNotional ?? 0m,
                dto.MinimumCashReserve ?? 0m,
                dto.QuantityTolerance ?? 0m,
                dto.MaximumDecisionAgeSeconds.HasValue
                    ? TimeSpan.FromSeconds(
                        (double)
                        dto.MaximumDecisionAgeSeconds.Value)
                    : null);

    private static string ComputeFingerprint(
        string json)
    {
        var hash =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(json));

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private sealed class RebalanceInputDto
    {
        public int? SchemaVersion { get; init; }
        public InstrumentDto? Instrument { get; init; }
        public List<MarketDataBarDto>? HistoricalDailyMarketBars { get; init; }
        public decimal? InitialCash { get; init; }
        public PortfolioDto? CurrentPortfolio { get; init; }
        public decimal? CurrentReferencePrice { get; init; }
        public ExecutionCostsDto? ExecutionCosts { get; init; }
        public ConstraintsDto? BacktestConstraints { get; init; }
        public ConstraintsDto? CurrentRebalanceConstraints { get; init; }
    }

    private sealed class InstrumentDto
    {
        public string? Symbol { get; init; }
        public string? AssetClass { get; init; }
        public string? Currency { get; init; }
        public string? VenueInstrumentId { get; init; }
        public string? Exchange { get; init; }
    }

    private sealed class MarketDataBarDto
    {
        public DateTimeOffset? OpenTime { get; init; }
        public DateTimeOffset? CloseTime { get; init; }
        public decimal? Open { get; init; }
        public decimal? High { get; init; }
        public decimal? Low { get; init; }
        public decimal? Close { get; init; }
    }

    private sealed class PortfolioDto
    {
        public string? BaseCurrency { get; init; }
        public decimal? NetAssetValue { get; init; }
        public decimal? Cash { get; init; }
        public List<PortfolioPositionDto>? Positions { get; init; }
        public DateTimeOffset? AsOf { get; init; }
    }

    private sealed class PortfolioPositionDto
    {
        public decimal? Quantity { get; init; }
    }

    private sealed class ExecutionCostsDto
    {
        public decimal? CommissionPerOrder { get; init; }
        public decimal? SlippageBasisPoints { get; init; }
    }

    private sealed class ConstraintsDto
    {
        public decimal? MaxTargetWeight { get; init; }
        public decimal? MinimumTradeNotional { get; init; }
        public decimal? MinimumCashReserve { get; init; }
        public decimal? QuantityTolerance { get; init; }
        public decimal? MaximumDecisionAgeSeconds { get; init; }
    }
}

public sealed record TranscriptResearchRebalanceDemoExecution(
    TranscriptResearchRebalanceInput Input,
    TranscriptResearchRebalanceDemoOutput Artifact);

public static class TranscriptResearchRebalanceDemoRunner
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

    public static TranscriptResearchRebalanceDemoExecution Create(
        TranscriptResearchDemoResolvedManifest manifest,
        EarningsResearchPolicyDefinition policy,
        string policyFingerprint,
        string priorNormalized,
        string priorStructured,
        string currentNormalized,
        string currentStructured,
        string rebalanceInputPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);

        var input =
            TranscriptResearchRebalanceInputLoader
                .Load(
                    rebalanceInputPath,
                    manifest.Instrument);
        var transcriptRequest =
            new TranscriptResearchDecisionDemoRequest(
                new TranscriptResearchArtifactBundle(
                    priorNormalized,
                    priorStructured,
                    manifest.Prior.Context),
                new TranscriptResearchArtifactBundle(
                    currentNormalized,
                    currentStructured,
                    manifest.Current.Context),
                policy);
        var result =
            new TranscriptResearchToRebalanceDemoService()
                .Run(
                    new TranscriptResearchToRebalanceDemoRequest(
                        transcriptRequest,
                        input.HistoricalDailyMarketBars,
                        input.InitialCash,
                        input.CurrentPortfolio,
                        input.CurrentReferencePrice,
                        input.ExecutionCosts,
                        input.BacktestConstraints,
                        input.CurrentRebalanceConstraints));

        if (!string.Equals(
                result.TranscriptResearch.PolicyFingerprint,
                policyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Policy fingerprint changed during VS-13 composition.");
        }

        var transcriptArtifact =
            TranscriptResearchDemoOutputFactory
                .Create(
                    manifest,
                    policy,
                    result.TranscriptResearch);
        var downstream =
            result.ResearchToRebalance;
        var artifact =
            new TranscriptResearchRebalanceDemoOutput(
                1,
                input.Provenance,
                transcriptArtifact.Policy,
                transcriptArtifact.Instrument,
                transcriptArtifact.Prior,
                transcriptArtifact.Current,
                transcriptArtifact.Assessment,
                downstream.LatestDecision,
                downstream.BacktestPeriodStart,
                downstream.BacktestPeriodEnd,
                downstream.Backtest.Metrics,
                downstream.Backtest.FinalPortfolio,
                downstream.CurrentRebalancePlan);
        return new TranscriptResearchRebalanceDemoExecution(
            input,
            artifact);
    }

    public static int Run(
        TranscriptResearchDemoResolvedManifest manifest,
        EarningsResearchPolicyDefinition policy,
        string policyFingerprint,
        string priorNormalized,
        string priorStructured,
        string currentNormalized,
        string currentStructured,
        string rebalanceInputPath,
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
                rebalanceInputPath);
        var artifact =
            execution.Artifact;
        var outputPath =
            ResolveJsonOutputPath(
                jsonPath,
                manifest,
                rebalanceInputPath);
        var directory =
            Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(
                artifact,
                JsonOptions));

        output.WriteLine(
            "TRANSCRIPT RESEARCH REBALANCE DEMO: PASS");
        output.WriteLine(
            "Instrument: " +
            artifact.Instrument.Symbol);
        output.WriteLine(
            "Prior event: " +
            artifact.Prior.EventId);
        output.WriteLine(
            "Current event: " +
            artifact.Current.EventId);
        output.WriteLine(
            "Assessment: " +
            artifact.Assessment.Assessment);
        output.WriteLine(
            "Rebalance status: " +
            artifact.CurrentRebalancePlan.Status);
        output.WriteLine(
            "Policy fingerprint: " +
            artifact.Policy.Fingerprint);
        output.WriteLine(
            "JSON artifact: " +
            outputPath);

        return 0;
    }

    private static string ResolveJsonOutputPath(
        string? jsonPath,
        TranscriptResearchDemoResolvedManifest manifest,
        string rebalanceInputPath)
    {
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            return Path.GetFullPath(jsonPath);
        }

        var directory =
            Path.GetDirectoryName(
                manifest.Prior.NormalizedDocumentPath)
            ?? Path.GetDirectoryName(
                Path.GetFullPath(rebalanceInputPath));

        return Path.Combine(
            directory
            ?? throw new InvalidDataException(
                "VS-13 output directory could not be resolved."),
            "transcript-research-rebalance-result.json");
    }
}
