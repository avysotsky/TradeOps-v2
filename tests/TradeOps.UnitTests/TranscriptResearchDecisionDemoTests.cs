using System.Text.Json;
using System.Text.Json.Nodes;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.TranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TranscriptResearchDecisionDemoTests
{
    [Fact]
    public void Run_valid_artifacts_reuses_existing_pipeline_and_preserves_audit_identity()
    {
        var fixture =
            LoadFixture();

        var result =
            new TranscriptResearchDecisionDemoService()
                .Run(
                    fixture.Request);

        Assert.Same(
            fixture.Manifest.Instrument,
            result.PriorInput.Instrument);
        Assert.Same(
            fixture.Manifest.Instrument,
            result.PriorEvent.Instrument);
        Assert.Same(
            fixture.Manifest.Instrument,
            result.CurrentInput.Instrument);
        Assert.Same(
            fixture.Manifest.Instrument,
            result.CurrentEvent.Instrument);

        Assert.Equal(
            "textdoc:sample-2026-q1",
            result.PriorInput.DocFlowDocumentId);
        Assert.Equal(
            new string(
                'a',
                64),
            result.PriorFactSet.DocFlowFingerprint);
        Assert.Equal(
            "textdoc:sample-2026-q2",
            result.CurrentInput.DocFlowDocumentId);
        Assert.Equal(
            new string(
                'b',
                64),
            result.CurrentFactSet.DocFlowFingerprint);
        Assert.Equal(
            "schema_driven_text_v1:sample_backend",
            result.CurrentFactSet.ExtractionEngine);
        Assert.Equal(
            new[]
            {
                "textseg:sample-q2-1"
            },
            result.CurrentFactSet
                .Revenue!
                .EvidenceSegmentIds);
        Assert.Equal(
            new[]
            {
                "textseg:sample-q2-2"
            },
            result.CurrentFactSet
                .OperatingMargin!
                .EvidenceSegmentIds);

        Assert.Equal(
            "sample-earnings-2026-q1",
            result.PriorEvent.EventId);
        Assert.Equal(
            "FY2026-Q1",
            result.PriorEvent.FiscalPeriod);
        Assert.Equal(
            "sample-earnings-2026-q2",
            result.CurrentEvent.EventId);
        Assert.Equal(
            "FY2026-Q2",
            result.CurrentEvent.FiscalPeriod);
        Assert.Equal(
            EarningsTranscriptEventFactory
                .ExtractionMethodName,
            result.CurrentEvent.Provenance
                .ExtractionMethod);

        var settings =
            EarningsResearchPolicyConfiguration
                .ToDecisionRuleSettings(
                    fixture.Policy);
        var targetWeights =
            EarningsResearchPolicyConfiguration
                .ToTargetWeightPolicy(
                    fixture.Policy);
        var directAssessment =
            DeterministicEarningsDecisionRule
                .Assess(
                    result.CurrentEvent,
                    result.PriorEvent,
                    settings);
        var expectedGeneratedAt =
            LaterOf(
                result.CurrentEvent.PublishedAt
                    .ToUniversalTime(),
                result.CurrentEvent.Provenance
                    .RetrievedAt
                    .ToUniversalTime());
        var directDecision =
            DeterministicEarningsDecisionRule
                .Evaluate(
                    result.CurrentEvent,
                    result.PriorEvent,
                    expectedGeneratedAt,
                    targetWeights,
                    fixture.Policy.StrategyId,
                    settings);

        Assert.Equal(
            directAssessment,
            result.Assessment);
        Assert.Equal(
            directDecision.DecisionId,
            result.Decision.DecisionId);
        Assert.Equal(
            directDecision.Action,
            result.Decision.Action);
        Assert.Equal(
            directDecision.TargetWeight,
            result.Decision.TargetWeight);
        Assert.Equal(
            directDecision.GeneratedAt,
            result.Decision.GeneratedAt);
        Assert.Equal(
            fixture.Policy.StrategyId,
            result.Decision.StrategyId);
        Assert.Equal(
            fixture.PolicyFingerprint,
            result.PolicyFingerprint);
        Assert.Equal(
            expectedGeneratedAt,
            result.Decision.GeneratedAt);
        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            result.Decision.Action);
        Assert.Equal(
            0.40m,
            result.Decision.TargetWeight);
    }

    [Fact]
    public void Run_invalid_normalized_artifact_rejects()
    {
        var fixture =
            LoadFixture();
        var request =
            fixture.Request with
            {
                Current =
                    fixture.Request.Current with
                    {
                        NormalizedDocumentJson =
                            "{}"
                    }
            };

        Assert.Throws<InvalidDataException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_structured_identity_rejects()
    {
        var fixture =
            LoadFixture();
        var root =
            JsonNode.Parse(
                fixture.Request.Current
                    .StructuredExtractionJson)!
                .AsObject();

        root["data"]!["docflow_fingerprint"] =
            new string(
                'c',
                64);

        var request =
            fixture.Request with
            {
                Current =
                    fixture.Request.Current with
                    {
                        StructuredExtractionJson =
                            root.ToJsonString()
                    }
            };

        Assert.Throws<InvalidDataException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_unknown_evidence_segment_rejects()
    {
        var fixture =
            LoadFixture();
        var root =
            JsonNode.Parse(
                fixture.Request.Current
                    .StructuredExtractionJson)!
                .AsObject();

        root["data"]!["facts"]!["revenue"]!["evidence_segment_ids"] =
            JsonNode.Parse(
                """["textseg:unknown"]""");

        var request =
            fixture.Request with
            {
                Current =
                    fixture.Request.Current with
                    {
                        StructuredExtractionJson =
                            root.ToJsonString()
                    }
            };

        Assert.Throws<InvalidDataException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_instrument_mismatch_rejects_before_decision()
    {
        var fixture =
            LoadFixture();
        var mismatchedInstrument =
            fixture.Manifest.Instrument with
            {
                Symbol = "DIFF"
            };
        var request =
            fixture.Request with
            {
                Current =
                    fixture.Request.Current with
                    {
                        Context =
                            fixture.Request.Current.Context with
                            {
                                Instrument =
                                    mismatchedInstrument
                            }
                    }
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_non_increasing_event_time_rejects()
    {
        var fixture =
            LoadFixture();
        var request =
            fixture.Request with
            {
                Current =
                    new TranscriptResearchArtifactBundle(
                        fixture.Request.Prior
                            .NormalizedDocumentJson,
                        fixture.Request.Prior
                            .StructuredExtractionJson,
                        fixture.Request.Current
                            .Context)
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_policy_rejects_through_existing_configuration()
    {
        var fixture =
            LoadFixture();
        var request =
            fixture.Request with
            {
                Policy =
                    fixture.Policy with
                    {
                        SchemaVersion = 2
                    }
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchDecisionDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Manifest_loader_resolves_artifacts_relative_to_manifest_directory()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var manifest =
            TranscriptResearchDemoManifestLoader
                .Load(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json"));

        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(
                    sampleDirectory,
                    "prior-normalized.json")),
            manifest.Prior
                .NormalizedDocumentPath);
        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(
                    sampleDirectory,
                    "prior-structured.json")),
            manifest.Prior
                .StructuredExtractionPath);
        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(
                    sampleDirectory,
                    "current-normalized.json")),
            manifest.Current
                .NormalizedDocumentPath);
        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(
                    sampleDirectory,
                    "current-structured.json")),
            manifest.Current
                .StructuredExtractionPath);
    }

    [Fact]
    public void Manifest_loader_unknown_member_rejects()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var root =
            JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json")))!
                .AsObject();
        root["unexpected"] =
            true;

        var path =
            WriteTemporaryFile(
                "manifest.json",
                root.ToJsonString());

        try
        {
            Assert.Throws<InvalidDataException>(
                () =>
                    TranscriptResearchDemoManifestLoader
                        .Load(
                            path));
        }
        finally
        {
            DeleteTemporaryDirectory(
                path);
        }
    }

    [Fact]
    public void Manifest_loader_invalid_instrument_rejects()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var root =
            JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json")))!
                .AsObject();
        root["instrument"]!["currency"] =
            "usd";

        var path =
            WriteTemporaryFile(
                "manifest.json",
                root.ToJsonString());

        try
        {
            Assert.Throws<InvalidDataException>(
                () =>
                    TranscriptResearchDemoManifestLoader
                        .Load(
                            path));
        }
        finally
        {
            DeleteTemporaryDirectory(
                path);
        }
    }

    [Fact]
    public void Manifest_loader_unsupported_schema_rejects()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var root =
            JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json")))!
                .AsObject();
        root["schemaVersion"] =
            2;

        var path =
            WriteTemporaryFile(
                "manifest.json",
                root.ToJsonString());

        try
        {
            Assert.Throws<InvalidDataException>(
                () =>
                    TranscriptResearchDemoManifestLoader
                        .Load(
                            path));
        }
        finally
        {
            DeleteTemporaryDirectory(
                path);
        }
    }

    [Fact]
    public void Cli_success_writes_deterministic_json_without_transcript_text()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var tempDirectory =
            CreateTemporaryDirectory();
        var firstPath =
            Path.Combine(
                tempDirectory,
                "first.json");
        var secondPath =
            Path.Combine(
                tempDirectory,
                "second.json");
        var firstOut =
            new StringWriter();
        var firstError =
            new StringWriter();
        var secondOut =
            new StringWriter();
        var secondError =
            new StringWriter();

        try
        {
            var firstExit =
                TranscriptResearchDemoCli.Run(
                    new[]
                    {
                        "--manifest",
                        Path.Combine(
                            sampleDirectory,
                            "manifest.json"),
                        "--policy",
                        Path.Combine(
                            sampleDirectory,
                            "policy.json"),
                        "--json",
                        firstPath
                    },
                    firstOut,
                    firstError);

            var secondExit =
                TranscriptResearchDemoCli.Run(
                    new[]
                    {
                        "--manifest",
                        Path.Combine(
                            sampleDirectory,
                            "manifest.json"),
                        "--policy",
                        Path.Combine(
                            sampleDirectory,
                            "policy.json"),
                        "--json",
                        secondPath
                    },
                    secondOut,
                    secondError);

            Assert.Equal(
                0,
                firstExit);
            Assert.Equal(
                0,
                secondExit);
            Assert.Equal(
                string.Empty,
                firstError.ToString());
            Assert.Equal(
                string.Empty,
                secondError.ToString());
            Assert.Equal(
                File.ReadAllText(
                    firstPath),
                File.ReadAllText(
                    secondPath));

            var consoleText =
                firstOut.ToString();

            Assert.Contains(
                "TRANSCRIPT RESEARCH DEMO: PASS",
                consoleText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Revenue for the quarter was 110 million dollars.",
                consoleText,
                StringComparison.Ordinal);

            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(
                        firstPath));

            Assert.Equal(
                "sample-earnings-2026-q2",
                document.RootElement
                    .GetProperty("current")
                    .GetProperty("eventId")
                    .GetString());
            Assert.Equal(
                "schema_driven_text_v1:sample_backend",
                document.RootElement
                    .GetProperty("current")
                    .GetProperty("extractionEngine")
                    .GetString());
            Assert.Equal(
                "2026-07-22T21:15:00+00:00",
                document.RootElement
                    .GetProperty("researchDecision")
                    .GetProperty("generatedAt")
                    .GetString());
            Assert.Equal(
                "textseg:sample-q2-1",
                document.RootElement
                    .GetProperty("current")
                    .GetProperty("evidence")
                    .GetProperty("revenue")
                    .EnumerateArray()
                    .Single()
                    .GetString());
        }
        finally
        {
            Directory.Delete(
                tempDirectory,
                recursive: true);
        }
    }

    [Fact]
    public void Cli_missing_artifact_returns_nonzero()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var root =
            JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json")))!
                .AsObject();
        root["prior"]!["normalizedDocument"] =
            "missing-normalized.json";

        var manifestPath =
            WriteTemporaryFile(
                "manifest.json",
                root.ToJsonString());
        var output =
            new StringWriter();
        var error =
            new StringWriter();

        try
        {
            var exitCode =
                TranscriptResearchDemoCli.Run(
                    new[]
                    {
                        "--manifest",
                        manifestPath,
                        "--policy",
                        Path.Combine(
                            sampleDirectory,
                            "policy.json")
                    },
                    output,
                    error);

            Assert.NotEqual(
                0,
                exitCode);
            Assert.Contains(
                "FAIL",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(
                manifestPath);
        }
    }

    [Fact]
    public void Cli_invalid_policy_returns_nonzero_before_composition()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var invalidPolicyPath =
            WriteTemporaryFile(
                "policy.json",
                """
                {
                  "schemaVersion": 2,
                  "strategyId": "invalid-policy",
                  "thresholds": {
                    "revenueGrowth": 0.05,
                    "dilutedEpsGrowth": 0.05,
                    "operatingMarginDelta": 0.01,
                    "minimumDirectionalSignals": 2
                  },
                  "targetWeights": {
                    "positive": 0.40,
                    "neutral": 0.20,
                    "negative": 0.00
                  }
                }
                """);
        var output =
            new StringWriter();
        var error =
            new StringWriter();

        try
        {
            var exitCode =
                TranscriptResearchDemoCli.Run(
                    new[]
                    {
                        "--manifest",
                        Path.Combine(
                            sampleDirectory,
                            "manifest.json"),
                        "--policy",
                        invalidPolicyPath
                    },
                    output,
                    error);

            Assert.NotEqual(
                0,
                exitCode);
            Assert.Contains(
                EarningsResearchPolicyValidationCodes
                    .UnsupportedSchemaVersion,
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(
                invalidPolicyPath);
        }
    }

    [Fact]
    public void Boundary_has_no_clock_network_process_or_external_project_dependency()
    {
        var toolAssembly =
            typeof(TranscriptResearchDemoCli)
                .Assembly;
        var references =
            toolAssembly
                .GetReferencedAssemblies()
                .Select(
                    item =>
                        item.Name)
                .Where(
                    item =>
                        item is not null)
                .ToArray();

        Assert.DoesNotContain(
            "System.Net.Http",
            references);
        Assert.DoesNotContain(
            "System.Diagnostics.Process",
            references);
        Assert.DoesNotContain(
            references,
            item =>
                item!.StartsWith(
                    "DocFlow",
                    StringComparison.Ordinal));

        var repositoryRoot =
            FindRepositoryRoot();
        var applicationSource =
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "src",
                    "TradeOps.Application",
                    "Services",
                    "TranscriptResearchDecisionDemoService.cs"));
        var toolSource =
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "TradeOps.TranscriptResearchDemo",
                    "TranscriptResearchDemo.cs"));
        var toolProject =
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "TradeOps.TranscriptResearchDemo",
                    "TradeOps.TranscriptResearchDemo.csproj"));

        Assert.DoesNotContain(
            "UtcNow",
            applicationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DateTime.Now",
            applicationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DateTimeOffset.Now",
            applicationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpClient",
            toolSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Process.Start",
            toolSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProcessStartInfo",
            toolSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DocFlow.csproj",
            toolProject,
            StringComparison.Ordinal);

        Assert.Equal(
            typeof(ResearchDecision),
            typeof(TranscriptResearchDecisionDemoResult)
                .GetProperty(
                    nameof(
                        TranscriptResearchDecisionDemoResult
                            .Decision))!
                .PropertyType);
    }

    private static Fixture LoadFixture()
    {
        var sampleDirectory =
            GetSampleDirectory();
        var manifest =
            TranscriptResearchDemoManifestLoader
                .Load(
                    Path.Combine(
                        sampleDirectory,
                        "manifest.json"));
        var policyLoad =
            EarningsResearchPolicyConfiguration
                .Load(
                    File.ReadAllText(
                        Path.Combine(
                            sampleDirectory,
                            "policy.json")));

        Assert.True(
            policyLoad.IsValid);
        Assert.NotNull(
            policyLoad.Definition);
        Assert.NotNull(
            policyLoad.Fingerprint);

        var request =
            new TranscriptResearchDecisionDemoRequest(
                new TranscriptResearchArtifactBundle(
                    File.ReadAllText(
                        manifest.Prior
                            .NormalizedDocumentPath),
                    File.ReadAllText(
                        manifest.Prior
                            .StructuredExtractionPath),
                    manifest.Prior.Context),
                new TranscriptResearchArtifactBundle(
                    File.ReadAllText(
                        manifest.Current
                            .NormalizedDocumentPath),
                    File.ReadAllText(
                        manifest.Current
                            .StructuredExtractionPath),
                    manifest.Current.Context),
                policyLoad.Definition!);

        return new Fixture(
            manifest,
            policyLoad.Definition!,
            policyLoad.Fingerprint!,
            request);
    }

    private static string GetSampleDirectory() =>
        Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "research",
            "transcript-research");

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

        throw new InvalidOperationException(
            "TradeOps repository root was not found.");
    }

    private static string WriteTemporaryFile(
        string fileName,
        string content)
    {
        var directory =
            CreateTemporaryDirectory();
        var path =
            Path.Combine(
                directory,
                fileName);

        File.WriteAllText(
            path,
            content);

        return path;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-vs08-{Guid.NewGuid():N}");

        Directory.CreateDirectory(
            directory);

        return directory;
    }

    private static void DeleteTemporaryDirectory(
        string path)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory)
            && Directory.Exists(
                directory))
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    private static DateTimeOffset LaterOf(
        DateTimeOffset left,
        DateTimeOffset right) =>
        right > left
            ? right
            : left;

    private sealed record Fixture(
        TranscriptResearchDemoResolvedManifest Manifest,
        EarningsResearchPolicyDefinition Policy,
        string PolicyFingerprint,
        TranscriptResearchDecisionDemoRequest Request);
}
