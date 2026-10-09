using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Application.Services;

namespace TradeOps.TranscriptResearchDemo;

public sealed record TranscriptResearchDemoPolicyOutput(
    int SchemaVersion,
    string StrategyId,
    string Fingerprint);

public sealed record TranscriptResearchDemoInstrumentOutput(
    string Symbol,
    string AssetClass,
    string? Currency,
    string? VenueInstrumentId,
    string? Exchange);

public sealed record TranscriptResearchDemoSourceOutput(
    string Provider,
    string SourceUri,
    string? SourceDocumentId,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset RetrievedAt,
    string ExtractionMethod,
    string? IssuerId);

public sealed record TranscriptResearchDemoFactValuesOutput(
    decimal? Revenue,
    decimal? DilutedEps,
    decimal? NetIncome,
    decimal? GrossMargin,
    decimal? OperatingMargin,
    string? GuidanceDirection);

public sealed record TranscriptResearchDemoEvidenceOutput(
    IReadOnlyList<string>? Revenue,
    IReadOnlyList<string>? DilutedEps,
    IReadOnlyList<string>? NetIncome,
    IReadOnlyList<string>? GrossMargin,
    IReadOnlyList<string>? OperatingMargin,
    IReadOnlyList<string>? GuidanceDirection,
    IReadOnlyList<string>? GuidanceRevenueLow,
    IReadOnlyList<string>? GuidanceRevenueHigh,
    IReadOnlyList<string>? GuidanceDilutedEpsLow,
    IReadOnlyList<string>? GuidanceDilutedEpsHigh);

public sealed record TranscriptResearchDemoArtifactOutput(
    string EventId,
    string FiscalPeriod,
    DateTimeOffset PublishedAt,
    TranscriptResearchDemoSourceOutput Source,
    string DocFlowDocumentId,
    string DocFlowFingerprint,
    string ExtractionEngine,
    decimal? ExtractionConfidence,
    TranscriptResearchDemoFactValuesOutput Facts,
    TranscriptResearchDemoEvidenceOutput Evidence);

public sealed record TranscriptResearchDemoAssessmentOutput(
    string Assessment,
    int Score,
    int ComparableSignals,
    decimal? RevenueGrowth,
    decimal? DilutedEpsGrowth,
    decimal? OperatingMarginDelta,
    decimal Confidence);

public sealed record TranscriptResearchDemoDecisionOutput(
    string DecisionId,
    string StrategyId,
    string Action,
    decimal? TargetWeight,
    decimal? Confidence,
    DateTimeOffset GeneratedAt,
    string? SourceEventId,
    string? Reason);

public sealed record TranscriptResearchDemoOutput(
    TranscriptResearchDemoPolicyOutput Policy,
    TranscriptResearchDemoInstrumentOutput Instrument,
    TranscriptResearchDemoArtifactOutput Prior,
    TranscriptResearchDemoArtifactOutput Current,
    TranscriptResearchDemoAssessmentOutput Assessment,
    TranscriptResearchDemoDecisionOutput ResearchDecision);

public static class TranscriptResearchDemoOutputFactory
{
    public static TranscriptResearchDemoOutput Create(
        TranscriptResearchDemoResolvedManifest manifest,
        EarningsResearchPolicyDefinition policy,
        TranscriptResearchDecisionDemoResult result)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(result);

        return new TranscriptResearchDemoOutput(
            new TranscriptResearchDemoPolicyOutput(
                policy.SchemaVersion,
                policy.StrategyId,
                result.PolicyFingerprint),
            new TranscriptResearchDemoInstrumentOutput(
                result.Decision.Instrument.Symbol,
                result.Decision.Instrument.AssetClass.ToString(),
                result.Decision.Instrument.Currency,
                result.Decision.Instrument.VenueInstrumentId,
                result.Decision.Instrument.Exchange),
            ToArtifact(
                result.PriorInput,
                result.PriorFactSet,
                result.PriorEvent),
            ToArtifact(
                result.CurrentInput,
                result.CurrentFactSet,
                result.CurrentEvent),
            new TranscriptResearchDemoAssessmentOutput(
                result.Assessment.Assessment.ToString(),
                result.Assessment.Score,
                result.Assessment.ComparableSignals,
                result.Assessment.RevenueGrowth,
                result.Assessment.DilutedEpsGrowth,
                result.Assessment.OperatingMarginDelta,
                result.Assessment.Confidence),
            new TranscriptResearchDemoDecisionOutput(
                result.Decision.DecisionId,
                result.Decision.StrategyId,
                result.Decision.Action.ToString(),
                result.Decision.TargetWeight,
                result.Decision.Confidence,
                result.Decision.GeneratedAt,
                result.Decision.SourceEventId,
                result.Decision.Reason));
    }

    private static TranscriptResearchDemoArtifactOutput ToArtifact(
        EarningsTranscriptResearchInput input,
        EarningsTranscriptFactSet facts,
        EarningsEvent earningsEvent)
    {
        var guidance =
            facts.Guidance;

        return new TranscriptResearchDemoArtifactOutput(
            earningsEvent.EventId,
            earningsEvent.FiscalPeriod,
            earningsEvent.PublishedAt.ToUniversalTime(),
            new TranscriptResearchDemoSourceOutput(
                earningsEvent.Provenance.Provider,
                earningsEvent.Provenance.SourceUri.AbsoluteUri,
                earningsEvent.Provenance.SourceDocumentId,
                earningsEvent.Provenance.SourceTimestamp.ToUniversalTime(),
                earningsEvent.Provenance.RetrievedAt.ToUniversalTime(),
                earningsEvent.Provenance.ExtractionMethod,
                earningsEvent.Provenance.IssuerId),
            input.DocFlowDocumentId,
            input.DocFlowFingerprint,
            facts.ExtractionEngine,
            facts.ExtractionConfidence,
            new TranscriptResearchDemoFactValuesOutput(
                facts.Revenue?.Value,
                facts.DilutedEps?.Value,
                facts.NetIncome?.Value,
                facts.GrossMargin?.Value,
                facts.OperatingMargin?.Value,
                guidance?.Direction?.Direction.ToString()),
            new TranscriptResearchDemoEvidenceOutput(
                CopyEvidence(
                    facts.Revenue),
                CopyEvidence(
                    facts.DilutedEps),
                CopyEvidence(
                    facts.NetIncome),
                CopyEvidence(
                    facts.GrossMargin),
                CopyEvidence(
                    facts.OperatingMargin),
                CopyEvidence(
                    guidance?.Direction),
                CopyEvidence(
                    guidance?.RevenueLow),
                CopyEvidence(
                    guidance?.RevenueHigh),
                CopyEvidence(
                    guidance?.DilutedEpsLow),
                CopyEvidence(
                    guidance?.DilutedEpsHigh)));
    }

    private static IReadOnlyList<string>? CopyEvidence(
        EarningsTranscriptNumericFact? fact) =>
        fact?.EvidenceSegmentIds.ToArray();

    private static IReadOnlyList<string>? CopyEvidence(
        EarningsTranscriptGuidanceDirectionFact? fact) =>
        fact?.EvidenceSegmentIds.ToArray();
}

public static class TranscriptResearchDemoCli
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented =
                true
        };

    public static int Run(
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        output ??=
            Console.Out;
        error ??=
            Console.Error;

        try
        {
            var options =
                ParseArgs(
                    args);

            var manifest =
                TranscriptResearchDemoManifestLoader
                    .Load(
                        options.ManifestPath);

            var policyJson =
                ReadRequiredFile(
                    options.PolicyPath,
                    "Policy");
            var policyLoad =
                EarningsResearchPolicyConfiguration
                    .Load(
                        policyJson);

            if (!policyLoad.IsValid
                || policyLoad.Definition is null
                || string.IsNullOrWhiteSpace(
                    policyLoad.Fingerprint))
            {
                var errors =
                    string.Join(
                        "; ",
                        policyLoad.Validation.Errors.Select(
                            item =>
                                $"{item.Code} at {item.Path}: {item.Message}"));

                throw new InvalidDataException(
                    $"Earnings research policy is invalid. {errors}");
            }

            var priorNormalized =
                ReadRequiredFile(
                    manifest.Prior.NormalizedDocumentPath,
                    "Prior normalized document");
            var priorStructured =
                ReadRequiredFile(
                    manifest.Prior.StructuredExtractionPath,
                    "Prior structured extraction");
            var currentNormalized =
                ReadRequiredFile(
                    manifest.Current.NormalizedDocumentPath,
                    "Current normalized document");
            var currentStructured =
                ReadRequiredFile(
                    manifest.Current.StructuredExtractionPath,
                    "Current structured extraction");

            if (options.ExecutionDryRun)
            {
                return TranscriptResearchExecutionDryRunDemoRunner
                    .Run(
                        manifest,
                        policyLoad.Definition,
                        policyLoad.Fingerprint,
                        priorNormalized,
                        priorStructured,
                        currentNormalized,
                        currentStructured,
                        options.RebalanceInputPath!,
                        options.RiskPreviewInputPath!,
                        options.JsonPath,
                        output);
            }

            if (!string.IsNullOrWhiteSpace(
                    options.RiskPreviewInputPath))
            {
                return TranscriptResearchRiskPreviewDemoRunner
                    .Run(
                        manifest,
                        policyLoad.Definition,
                        policyLoad.Fingerprint,
                        priorNormalized,
                        priorStructured,
                        currentNormalized,
                        currentStructured,
                        options.RebalanceInputPath!,
                        options.RiskPreviewInputPath,
                        options.JsonPath,
                        output);
            }

            if (!string.IsNullOrWhiteSpace(
                    options.RebalanceInputPath))
            {
                return TranscriptResearchRebalanceDemoRunner
                    .Run(
                        manifest,
                        policyLoad.Definition,
                        policyLoad.Fingerprint,
                        priorNormalized,
                        priorStructured,
                        currentNormalized,
                        currentStructured,
                        options.RebalanceInputPath,
                        options.JsonPath,
                        output);
            }

            var result =
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        new TranscriptResearchDecisionDemoRequest(
                            new TranscriptResearchArtifactBundle(
                                priorNormalized,
                                priorStructured,
                                manifest.Prior.Context),
                            new TranscriptResearchArtifactBundle(
                                currentNormalized,
                                currentStructured,
                                manifest.Current.Context),
                            policyLoad.Definition));

            if (!string.Equals(
                    result.PolicyFingerprint,
                    policyLoad.Fingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Policy fingerprint changed during composition.");
            }

            var artifact =
                TranscriptResearchDemoOutputFactory
                    .Create(
                        manifest,
                        policyLoad.Definition,
                        result);

            var jsonPath =
                ResolveJsonOutputPath(
                    options.JsonPath,
                    options.ManifestPath);

            var directory =
                Path.GetDirectoryName(
                    jsonPath);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(
                    artifact,
                    JsonOptions));

            output.WriteLine(
                "TRANSCRIPT RESEARCH DEMO: PASS");
            output.WriteLine(
                $"Instrument: {artifact.Instrument.Symbol}");
            output.WriteLine(
                $"Prior event: {artifact.Prior.EventId}");
            output.WriteLine(
                $"Current event: {artifact.Current.EventId}");
            output.WriteLine(
                $"Assessment: {artifact.Assessment.Assessment}");
            output.WriteLine(
                $"Target weight: {FormatNullable(artifact.ResearchDecision.TargetWeight)}");
            output.WriteLine(
                $"GeneratedAt: {artifact.ResearchDecision.GeneratedAt:O}");
            output.WriteLine(
                $"Policy fingerprint: {artifact.Policy.Fingerprint}");
            output.WriteLine(
                $"JSON artifact: {jsonPath}");

            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"TRANSCRIPT RESEARCH DEMO: FAIL ({exception.GetType().Name}: {exception.Message})");
            return 1;
        }
    }

    private static CliOptions ParseArgs(
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(
            args);

        string? manifestPath =
            null;
        string? policyPath =
            null;
        string? jsonPath =
            null;
        string? rebalanceInputPath =
            null;
        string? riskPreviewInputPath =
            null;
        var executionDryRun =
            false;

        for (var index = 0;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--manifest":
                    manifestPath =
                        RequireValue(
                            args,
                            ref index,
                            "--manifest");
                    break;

                case "--policy":
                    policyPath =
                        RequireValue(
                            args,
                            ref index,
                            "--policy");
                    break;

                case "--json":
                    jsonPath =
                        RequireValue(
                            args,
                            ref index,
                            "--json");
                    break;

                case "--rebalance-input":
                    rebalanceInputPath =
                        RequireValue(
                            args,
                            ref index,
                            "--rebalance-input");
                    break;

                case "--risk-preview-input":
                    riskPreviewInputPath =
                        RequireValue(
                            args,
                            ref index,
                            "--risk-preview-input");
                    break;

                case "--execution-dry-run":
                    executionDryRun =
                        true;
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[index]}'. Supported: --manifest <path>, --policy <path>, --json <path>, --rebalance-input <path>, --risk-preview-input <path>, --execution-dry-run.");
            }
        }

        if (string.IsNullOrWhiteSpace(
                manifestPath))
        {
            throw new ArgumentException(
                "--manifest <path> is required.");
        }

        if (string.IsNullOrWhiteSpace(
                policyPath))
        {
            throw new ArgumentException(
                "--policy <path> is required.");
        }

        if (executionDryRun
            && string.IsNullOrWhiteSpace(
                riskPreviewInputPath))
        {
            throw new ArgumentException(
                "--execution-dry-run requires --risk-preview-input.");
        }

        if (!string.IsNullOrWhiteSpace(
                riskPreviewInputPath)
            && string.IsNullOrWhiteSpace(
                rebalanceInputPath))
        {
            throw new ArgumentException(
                "--risk-preview-input requires --rebalance-input.");
        }

        return new CliOptions(
            manifestPath,
            policyPath,
            jsonPath,
            rebalanceInputPath,
            riskPreviewInputPath,
            executionDryRun);
    }

    private static string RequireValue(
        string[] args,
        ref int index,
        string option)
    {
        if (index + 1 >=
            args.Length
            || string.IsNullOrWhiteSpace(
                args[index + 1]))
        {
            throw new ArgumentException(
                $"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ReadRequiredFile(
        string path,
        string description)
    {
        if (!File.Exists(
                path))
        {
            throw new FileNotFoundException(
                $"{description} file was not found.",
                path);
        }

        try
        {
            return File.ReadAllText(
                path);
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"{description} file could not be read: {exception.Message}",
                exception);
        }
    }

    private static string ResolveJsonOutputPath(
        string? jsonPath,
        string manifestPath)
    {
        if (!string.IsNullOrWhiteSpace(
                jsonPath))
        {
            return Path.GetFullPath(
                jsonPath);
        }

        var manifestDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(
                    manifestPath))
            ?? throw new InvalidDataException(
                "Manifest directory could not be resolved.");

        return Path.Combine(
            manifestDirectory,
            "transcript-research-result.json");
    }

    private static string FormatNullable(
        decimal? value) =>
        value?.ToString(
            "0.########",
            CultureInfo.InvariantCulture)
        ?? "n/a";

    private sealed record CliOptions(
        string ManifestPath,
        string PolicyPath,
        string? JsonPath,
        string? RebalanceInputPath,
        string? RiskPreviewInputPath,
        bool ExecutionDryRun);
}
