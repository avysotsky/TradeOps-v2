using System.Text.Json.Nodes;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.TranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TranscriptResearchToRebalanceDemoTests
{
    [Fact]
    public void Run_composes_existing_transcript_research_backtest_and_rebalance_with_exact_equivalence()
    {
        var fixture =
            LoadFixture();
        var request =
            CreateRequest(
                fixture);
        var transcriptExpected =
            new TranscriptResearchDecisionDemoService()
                .Run(
                    fixture.TranscriptRequest);
        var downstreamExpected =
            RunDirectDownstream(
                request,
                transcriptExpected);

        var result =
            new TranscriptResearchToRebalanceDemoService()
                .Run(
                    request);

        Assert.Equal(
            "sample-earnings-2026-q1",
            result.TranscriptResearch
                .PriorEvent
                .EventId);
        Assert.Equal(
            "sample-earnings-2026-q2",
            result.TranscriptResearch
                .CurrentEvent
                .EventId);
        Assert.Equal(
            transcriptExpected.PriorEvent,
            result.TranscriptResearch
                .PriorEvent);
        Assert.Equal(
            transcriptExpected.CurrentEvent,
            result.TranscriptResearch
                .CurrentEvent);

        var expectedFingerprint =
            EarningsResearchPolicyConfiguration
                .ComputeFingerprint(
                    fixture.Policy);

        Assert.Equal(
            expectedFingerprint,
            result.TranscriptResearch
                .PolicyFingerprint);
        Assert.Equal(
            transcriptExpected.Assessment,
            result.ResearchToRebalance
                .LatestAssessment);
        AssertDecisionEquivalent(
            transcriptExpected.Decision,
            result.ResearchToRebalance
                .LatestDecision);

        var expectedGeneratedAt =
            LaterOf(
                result.TranscriptResearch
                    .CurrentEvent
                    .PublishedAt
                    .ToUniversalTime(),
                result.TranscriptResearch
                    .CurrentEvent
                    .Provenance
                    .RetrievedAt
                    .ToUniversalTime());

        Assert.Equal(
            expectedGeneratedAt,
            result.TranscriptResearch
                .Decision
                .GeneratedAt);
        Assert.Equal(
            expectedGeneratedAt,
            result.ResearchToRebalance
                .LatestDecision
                .GeneratedAt);
        Assert.Equal(
            fixture.Policy.StrategyId,
            result.ResearchToRebalance
                .LatestDecision
                .StrategyId);
        Assert.Equal(
            fixture.Policy.TargetWeights.Positive,
            result.ResearchToRebalance
                .LatestDecision
                .TargetWeight);

        Assert.Equal(
            1,
            result.ResearchToRebalance
                .EventCount);
        Assert.Equal(
            1,
            result.ResearchToRebalance
                .Backtest
                .Metrics
                .EventCount);
        Assert.Single(
            result.ResearchToRebalance
                .Backtest
                .Plans);
        Assert.Single(
            result.ResearchToRebalance
                .Backtest
                .Fills);
        Assert.Equal(
            downstreamExpected.Backtest.Metrics,
            result.ResearchToRebalance
                .Backtest
                .Metrics);
        AssertPortfolioEquivalent(
            downstreamExpected.Backtest
                .FinalPortfolio,
            result.ResearchToRebalance
                .Backtest
                .FinalPortfolio);

        AssertRebalancePlanEquivalent(
            downstreamExpected.CurrentRebalancePlan,
            result.ResearchToRebalance
                .CurrentRebalancePlan);
        Assert.Equal(
            downstreamExpected.CurrentRebalancePlan
                .OrderIntent,
            result.ResearchToRebalance
                .CurrentRebalancePlan
                .OrderIntent);
        Assert.NotNull(
            result.ResearchToRebalance
                .CurrentRebalancePlan
                .OrderIntent);
    }

    [Fact]
    public void Run_same_request_is_deterministic()
    {
        var request =
            CreateRequest(
                LoadFixture());
        var sut =
            new TranscriptResearchToRebalanceDemoService();

        var first =
            sut.Run(
                request);
        var second =
            sut.Run(
                request);

        Assert.Equal(
            first.TranscriptResearch.Assessment,
            second.TranscriptResearch.Assessment);
        AssertDecisionEquivalent(
            first.TranscriptResearch.Decision,
            second.TranscriptResearch.Decision);
        Assert.Equal(
            first.TranscriptResearch.PolicyFingerprint,
            second.TranscriptResearch.PolicyFingerprint);
        Assert.Equal(
            first.ResearchToRebalance.LatestAssessment,
            second.ResearchToRebalance.LatestAssessment);
        AssertDecisionEquivalent(
            first.ResearchToRebalance.LatestDecision,
            second.ResearchToRebalance.LatestDecision);
        Assert.Equal(
            first.ResearchToRebalance.Backtest.Metrics,
            second.ResearchToRebalance.Backtest.Metrics);
        Assert.Equal(
            first.ResearchToRebalance.Backtest
                .Fills
                .ToArray(),
            second.ResearchToRebalance.Backtest
                .Fills
                .ToArray());
        Assert.Equal(
            first.ResearchToRebalance.Backtest
                .EquityCurve
                .ToArray(),
            second.ResearchToRebalance.Backtest
                .EquityCurve
                .ToArray());
        AssertRebalancePlanEquivalent(
            first.ResearchToRebalance.CurrentRebalancePlan,
            second.ResearchToRebalance.CurrentRebalancePlan);
    }

    [Fact]
    public void Run_invalid_normalized_transcript_rejects_through_existing_transcript_path()
    {
        var fixture =
            LoadFixture();
        var request =
            CreateRequest(
                fixture) with
            {
                TranscriptResearch =
                    fixture.TranscriptRequest with
                    {
                        Current =
                            fixture.TranscriptRequest
                                .Current with
                            {
                                NormalizedDocumentJson =
                                    "{}"
                            }
                    }
            };

        Assert.Throws<InvalidDataException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_structured_extraction_rejects_through_existing_structured_facts_path()
    {
        var fixture =
            LoadFixture();
        var root =
            JsonNode.Parse(
                fixture.TranscriptRequest
                    .Current
                    .StructuredExtractionJson)!
                .AsObject();

        root["data"]!["docflow_fingerprint"] =
            new string(
                'c',
                64);

        var request =
            CreateRequest(
                fixture) with
            {
                TranscriptResearch =
                    fixture.TranscriptRequest with
                    {
                        Current =
                            fixture.TranscriptRequest
                                .Current with
                            {
                                StructuredExtractionJson =
                                    root.ToJsonString()
                            }
                    }
            };

        Assert.Throws<InvalidDataException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_mismatched_transcript_instrument_rejects_through_existing_transcript_composition()
    {
        var fixture =
            LoadFixture();
        var request =
            CreateRequest(
                fixture) with
            {
                TranscriptResearch =
                    fixture.TranscriptRequest with
                    {
                        Current =
                            fixture.TranscriptRequest
                                .Current with
                            {
                                Context =
                                    fixture.TranscriptRequest
                                        .Current
                                        .Context with
                                    {
                                        Instrument =
                                            fixture.Manifest
                                                .Instrument with
                                            {
                                                Symbol = "DIFF"
                                            }
                                    }
                            }
                    }
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_policy_rejects_through_existing_policy_configuration()
    {
        var fixture =
            LoadFixture();
        var request =
            CreateRequest(
                fixture) with
            {
                TranscriptResearch =
                    fixture.TranscriptRequest with
                    {
                        Policy =
                            fixture.Policy with
                            {
                                SchemaVersion = 2
                            }
                    }
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_insufficient_market_data_rejects_through_existing_backtester()
    {
        var request =
            CreateRequest(
                LoadFixture()) with
            {
                HistoricalDailyMarketBars =
                    Array.Empty<MarketDataBar>()
            };

        Assert.Throws<ArgumentException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_reference_price_rejects_through_existing_vs01_validation()
    {
        var request =
            CreateRequest(
                LoadFixture()) with
            {
                CurrentReferencePrice = 0m
            };

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request));
    }

    [Fact]
    public void Run_invalid_current_portfolio_rejects_through_existing_rebalance_planner()
    {
        var fixture =
            LoadFixture();
        var request =
            CreateRequest(
                fixture);
        var invalidPortfolio =
            request.CurrentPortfolio with
            {
                NetAssetValue = 0m
            };

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new TranscriptResearchToRebalanceDemoService()
                    .Run(
                        request with
                        {
                            CurrentPortfolio =
                                invalidPortfolio
                        }));
    }

    [Fact]
    public void Boundary_is_application_only_and_has_no_duplicate_backtester_planner_or_external_io()
    {
        var applicationAssembly =
            typeof(TranscriptResearchToRebalanceDemoService)
                .Assembly;
        var references =
            applicationAssembly
                .GetReferencedAssemblies()
                .Select(
                    item =>
                        item.Name)
                .Where(
                    item =>
                        item is not null)
                .ToArray();

        Assert.DoesNotContain(
            "TradeOps.TranscriptResearchDemo",
            references);
        var source =
            File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "src",
                    "TradeOps.Application",
                    "Services",
                    "TranscriptResearchToRebalanceDemoService.cs"));

        Assert.DoesNotContain(
            "UtcNow",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DateTime.Now",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DateTimeOffset.Now",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpClient",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProcessStartInfo",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Process.Start",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EventDrivenBacktester",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PortfolioRebalancePlanner",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BacktestReplayItem",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DocFlow",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenAI",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IBKR",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "TranscriptResearchDecisionDemoService",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResearchToRebalanceDemoService",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResearchDecisionTimingMode",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ObservedRetrieval",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "transcriptResult.PriorEvent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "transcriptResult.CurrentEvent",
            source,
            StringComparison.Ordinal);
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

        var transcriptRequest =
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
            transcriptRequest);
    }

    private static TranscriptResearchToRebalanceDemoRequest
        CreateRequest(
            Fixture fixture)
    {
        var currentPortfolio =
            new PortfolioSnapshot(
                "USD",
                NetAssetValue: 10_000m,
                Cash: 9_000m,
                Positions:
                    new[]
                    {
                        new PortfolioPosition(
                            fixture.Manifest
                                .Instrument,
                            Quantity: 10m)
                    },
                AsOf:
                    Utc(
                        2026,
                        7,
                        24,
                        22,
                        0));

        return new TranscriptResearchToRebalanceDemoRequest(
            fixture.TranscriptRequest,
            new[]
            {
                Bar(
                    fixture.Manifest.Instrument,
                    2026,
                    7,
                    23,
                    100m,
                    102m),
                Bar(
                    fixture.Manifest.Instrument,
                    2026,
                    7,
                    24,
                    102m,
                    104m)
            },
            InitialCash: 10_000m,
            currentPortfolio,
            CurrentReferencePrice: 100m,
            CurrentRebalanceConstraints:
                new RebalanceConstraints(
                    MaxTargetWeight: 0.50m));
    }

    private static ResearchToRebalanceDemoResult
        RunDirectDownstream(
            TranscriptResearchToRebalanceDemoRequest request,
            TranscriptResearchDecisionDemoResult transcript)
    {
        var policy =
            request.TranscriptResearch.Policy;

        return new ResearchToRebalanceDemoService()
            .Run(
                new ResearchToRebalanceDemoRequest(
                    new[]
                    {
                        transcript.PriorEvent,
                        transcript.CurrentEvent
                    },
                    request.HistoricalDailyMarketBars,
                    EarningsResearchPolicyConfiguration
                        .ToDecisionRuleSettings(
                            policy),
                    EarningsResearchPolicyConfiguration
                        .ToTargetWeightPolicy(
                            policy),
                    request.InitialCash,
                    request.CurrentPortfolio,
                    request.CurrentReferencePrice,
                    request.ExecutionCosts,
                    request.BacktestConstraints,
                    request.CurrentRebalanceConstraints,
                    policy.StrategyId,
                    ResearchDecisionTimingMode
                        .ObservedRetrieval));
    }

    private static void AssertDecisionEquivalent(
        ResearchDecision expected,
        ResearchDecision actual)
    {
        Assert.Equal(
            expected.DecisionId,
            actual.DecisionId);
        Assert.Equal(
            expected.StrategyId,
            actual.StrategyId);
        Assert.Equal(
            expected.Instrument,
            actual.Instrument);
        Assert.Equal(
            expected.Action,
            actual.Action);
        Assert.True(
            expected.GeneratedAt.EqualsExact(
                actual.GeneratedAt));
        Assert.Equal(
            expected.TargetWeight,
            actual.TargetWeight);
        Assert.Equal(
            expected.Confidence,
            actual.Confidence);
        Assert.Equal(
            expected.SourceEventId,
            actual.SourceEventId);
        Assert.Equal(
            expected.Reason,
            actual.Reason);
        Assert.Equal(
            expected.ValidUntil.HasValue,
            actual.ValidUntil.HasValue);

        if (expected.ValidUntil.HasValue)
        {
            Assert.True(
                expected.ValidUntil.Value.EqualsExact(
                    actual.ValidUntil!.Value));
        }

        if (expected.Metadata is null ||
            actual.Metadata is null)
        {
            Assert.Equal(
                expected.Metadata is null,
                actual.Metadata is null);
            return;
        }

        Assert.Equal(
            expected.Metadata.Count,
            actual.Metadata.Count);

        foreach (var item in expected.Metadata)
        {
            Assert.True(
                actual.Metadata.TryGetValue(
                    item.Key,
                    out var value));
            Assert.Equal(
                item.Value,
                value);
        }
    }

    private static void AssertPortfolioEquivalent(
        PortfolioSnapshot expected,
        PortfolioSnapshot actual)
    {
        Assert.Equal(
            expected.BaseCurrency,
            actual.BaseCurrency);
        Assert.Equal(
            expected.NetAssetValue,
            actual.NetAssetValue);
        Assert.Equal(
            expected.Cash,
            actual.Cash);
        Assert.Equal(
            expected.AsOf,
            actual.AsOf);
        Assert.Equal(
            expected.Positions.ToArray(),
            actual.Positions.ToArray());
    }

    private static void AssertRebalancePlanEquivalent(
        RebalancePlan expected,
        RebalancePlan actual)
    {
        Assert.Equal(
            expected.DecisionId,
            actual.DecisionId);
        Assert.Equal(
            expected.StrategyId,
            actual.StrategyId);
        Assert.Equal(
            expected.PlannedAt,
            actual.PlannedAt);
        Assert.Equal(
            expected.TargetPosition,
            actual.TargetPosition);
        Assert.Equal(
            expected.CurrentQuantity,
            actual.CurrentQuantity);
        Assert.Equal(
            expected.CurrentNotional,
            actual.CurrentNotional);
        Assert.Equal(
            expected.DeltaQuantity,
            actual.DeltaQuantity);
        Assert.Equal(
            expected.DeltaNotional,
            actual.DeltaNotional);
        Assert.Equal(
            expected.Status,
            actual.Status);
        Assert.Equal(
            expected.OrderIntent,
            actual.OrderIntent);
        Assert.Equal(
            expected.ConstraintViolations.ToArray(),
            actual.ConstraintViolations.ToArray());
    }

    private static MarketDataBar Bar(
        InstrumentReference instrument,
        int year,
        int month,
        int day,
        decimal open,
        decimal close)
    {
        var high =
            Math.Max(
                open,
                close) +
            1m;
        var low =
            Math.Min(
                open,
                close) -
            1m;

        return new MarketDataBar(
            instrument,
            MarketDataBarPeriod.Daily,
            Utc(
                year,
                month,
                day,
                14,
                30),
            Utc(
                year,
                month,
                day,
                21,
                0),
            open,
            high,
            low,
            close);
    }

    private static DateTimeOffset LaterOf(
        DateTimeOffset left,
        DateTimeOffset right) =>
        right > left
            ? right
            : left;

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(
            year,
            month,
            day,
            hour,
            minute,
            0,
            TimeSpan.Zero);

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

    private sealed record Fixture(
        TranscriptResearchDemoResolvedManifest Manifest,
        EarningsResearchPolicyDefinition Policy,
        TranscriptResearchDecisionDemoRequest TranscriptRequest);
}
