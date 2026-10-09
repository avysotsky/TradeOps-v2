using System.Text.Json;
using System.Text.Json.Nodes;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.ProviderTranscriptResearchDemo;
using TradeOps.TranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TranscriptResearchRiskPreviewTests
{
    [Fact]
    public void Sample_risk_input_is_strict_and_maps_expected_state()
    {
        var input =
            TranscriptResearchRiskPreviewInputLoader
                .Load(
                    SampleRiskInputPath());

        Assert.Equal(
            50m,
            input.Settings.MaxPositionSize);
        Assert.Equal(
            50m,
            input.Settings.MaxOrderSize);
        Assert.Equal(
            1000m,
            input.Settings.MaxDailyLoss);
        Assert.Equal(
            5,
            input.Settings.MaxOpenPositions);
        Assert.Contains(
            "SAMP",
            input.Settings.AllowedSymbols);
        Assert.True(
            input.ControlSnapshot.TradingEnabled);
        Assert.False(
            input.ControlSnapshot.EmergencyStop);
        Assert.Equal(
            "USD",
            input.ControlSnapshot.SettlementCurrency);
        Assert.Equal(
            0m,
            input.ControlSnapshot.DailyNetRealizedPnL);
        Assert.Equal(
            0,
            input.ControlSnapshot.ActivePositionMismatchCount);
    }

    [Fact]
    public void Risk_input_rejects_wrong_schema_case_unknown_member_and_malformed_json()
    {
        using var fixture =
            new TempFixture();

        var wrongVersion =
            fixture.MutateRiskInput(
                root =>
                    root["schemaVersion"] =
                        2);
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        wrongVersion));

        var wrongCase =
            fixture.MutateRiskInput(
                root =>
                {
                    var value =
                        root["maxOrderSize"]?
                            .DeepClone();
                    root.Remove(
                        "maxOrderSize");
                    root["MaxOrderSize"] =
                        value;
                });
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        wrongCase));

        var unknown =
            fixture.MutateRiskInput(
                root =>
                    root["unexpected"] =
                        true);
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        unknown));

        var malformed =
            Path.Combine(
                fixture.Root,
                "malformed.json");
        File.WriteAllText(
            malformed,
            "{");

        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        malformed));
    }

    [Fact]
    public void Risk_input_rejects_invalid_limits_blank_and_duplicate_symbols()
    {
        using var fixture =
            new TempFixture();

        var invalidLimit =
            fixture.MutateRiskInput(
                root =>
                    root["maxPositionSize"] =
                        0);
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        invalidLimit));

        var blank =
            fixture.MutateRiskInput(
                root =>
                    root["allowedSymbols"] =
                        new JsonArray(
                            JsonValue.Create(
                                "SAMP"),
                            JsonValue.Create(
                                " ")));
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        blank));

        var duplicate =
            fixture.MutateRiskInput(
                root =>
                    root["allowedSymbols"] =
                        new JsonArray(
                            JsonValue.Create(
                                "SAMP"),
                            JsonValue.Create(
                                "samp")));
        Assert.Throws<InvalidDataException>(
            () =>
                TranscriptResearchRiskPreviewInputLoader
                    .Load(
                        duplicate));
    }

    [Fact]
    public void Cli_requires_risk_value_and_rebalance_input()
    {
        using var output =
            new StringWriter();
        using var error =
            new StringWriter();

        var missingValue =
            TranscriptResearchDemoCli.Run(
                new[]
                {
                    "--manifest",
                    "manifest.json",
                    "--policy",
                    "policy.json",
                    "--risk-preview-input"
                },
                output,
                error);

        Assert.Equal(
            1,
            missingValue);
        Assert.Contains(
            "--risk-preview-input requires a value",
            error.ToString(),
            StringComparison.Ordinal);

        output.GetStringBuilder()
            .Clear();
        error.GetStringBuilder()
            .Clear();

        var withoutRebalance =
            TranscriptResearchDemoCli.Run(
                new[]
                {
                    "--manifest",
                    "manifest.json",
                    "--policy",
                    "policy.json",
                    "--risk-preview-input",
                    "risk.json"
                },
                output,
                error);

        Assert.Equal(
            1,
            withoutRebalance);
        Assert.Contains(
            "--risk-preview-input requires --rebalance-input",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_options_parse_risk_preview_and_require_rebalance()
    {
        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--docflow-root",
                        "docflow",
                        "--model",
                        "model",
                        "--rebalance-input",
                        "rebalance.json",
                        "--risk-preview-input",
                        "risk.json"
                    });

        Assert.Equal(
            "risk.json",
            options.RiskPreviewInputPath);

        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--docflow-root",
                            "docflow",
                            "--model",
                            "model",
                            "--risk-preview-input",
                            "risk.json"
                        }));

        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--docflow-root",
                            "docflow",
                            "--model",
                            "model",
                            "--rebalance-input",
                            "rebalance.json",
                            "--risk-preview-input"
                        }));
    }

    [Fact]
    public void Missing_provider_risk_file_fails_before_dotnet_or_provider_work()
    {
        using var fixture =
            new ProviderFixture();
        var runner =
            new RecordingProcessRunner();
        var harness =
            new ProviderTranscriptResearchDemoHarness(
                runner,
                new ApiKeyEnvironmentReader());

        var exitCode =
            harness.Run(
                fixture.Options(
                    Path.Combine(
                        fixture.TradeOpsRoot,
                        "missing-risk.json")),
                fixture.TradeOpsRoot,
                new StringWriter(),
                new StringWriter());

        Assert.Equal(
            1,
            exitCode);
        Assert.Empty(
            runner.PreflightInvocations);
        Assert.Empty(
            runner.Invocations);
    }

    [Fact]
    public void Provider_harness_forwards_risk_preview_to_existing_consumer_path()
    {
        using var fixture =
            new ProviderFixture();
        var runner =
            new RecordingProcessRunner();
        var output =
            new StringWriter();
        var error =
            new StringWriter();
        var harness =
            new ProviderTranscriptResearchDemoHarness(
                runner,
                new ApiKeyEnvironmentReader());

        var exitCode =
            harness.Run(
                fixture.Options(
                    fixture.RiskInput),
                fixture.TradeOpsRoot,
                output,
                error);

        Assert.Equal(
            0,
            exitCode);
        Assert.Single(
            runner.PreflightInvocations);
        Assert.Equal(
            3,
            runner.Invocations.Count);

        var consumer =
            runner.Invocations[2];

        Assert.Contains(
            "--rebalance-input",
            consumer.Arguments);
        Assert.Contains(
            fixture.RebalanceInput,
            consumer.Arguments);
        Assert.Contains(
            "--risk-preview-input",
            consumer.Arguments);
        Assert.Contains(
            fixture.RiskInput,
            consumer.Arguments);
        Assert.Contains(
            Path.Combine(
                fixture.WorkDirectory,
                "transcript-research-risk-preview-result.json"),
            consumer.Arguments);
        Assert.Contains(
            "Stage: TradeOps VS-16",
            output.ToString(),
            StringComparison.Ordinal);
    }


    [Fact]
    public void Provider_harness_forwards_execution_dry_run_to_vs17_without_changing_provider_stages()
    {
        using var fixture =
            new ProviderFixture();
        var runner =
            new RecordingProcessRunner();
        var output =
            new StringWriter();
        var error =
            new StringWriter();
        var harness =
            new ProviderTranscriptResearchDemoHarness(
                runner,
                new ApiKeyEnvironmentReader());

        var exitCode =
            harness.Run(
                fixture.Options(
                    fixture.RiskInput,
                    executionDryRun:
                        true),
                fixture.TradeOpsRoot,
                output,
                error);

        Assert.Equal(
            0,
            exitCode);
        Assert.Single(
            runner.PreflightInvocations);
        Assert.Equal(
            3,
            runner.Invocations.Count);

        var consumer =
            runner.Invocations[2];

        Assert.Contains(
            "--risk-preview-input",
            consumer.Arguments);
        Assert.Contains(
            "--execution-dry-run",
            consumer.Arguments);
        Assert.Contains(
            Path.Combine(
                fixture.WorkDirectory,
                "transcript-research-execution-dry-run-result.json"),
            consumer.Arguments);
        Assert.Contains(
            "Stage: TradeOps VS-17",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Intent_projection_is_exact_and_signal_id_is_deterministic()
    {
        var plan =
            CreatePlan();

        var first =
            RebalanceRiskPreviewSignalProjector
                .Project(
                    plan);
        var second =
            RebalanceRiskPreviewSignalProjector
                .Project(
                    plan);

        Assert.Equal(
            plan.OrderIntent!.Instrument.Symbol,
            first.Symbol);
        Assert.Equal(
            plan.OrderIntent.Side,
            first.Side);
        Assert.Equal(
            plan.OrderIntent.Quantity,
            first.RequestedQuantity);
        Assert.Equal(
            "RebalanceRiskPreview",
            first.SignalType);
        Assert.Equal(
            plan.PlannedAt,
            first.CreatedAt);
        Assert.Equal(
            "transcript-research-rebalance-risk-preview",
            first.Source);
        Assert.Null(
            first.RiskPercent);
        Assert.Null(
            first.StopLoss);
        Assert.Null(
            first.TakeProfit);
        Assert.NotEqual(
            Guid.Empty,
            first.Id);
        Assert.Equal(
            first.Id,
            second.Id);
    }

    [Fact]
    public async Task Current_portfolio_maps_to_read_only_exchange_positions()
    {
        var portfolio =
            CreatePortfolio();
        var exchange =
            new TranscriptResearchRiskPreviewExchangeClient(
                portfolio);

        var positions =
            await exchange.GetPositionsAsync();

        var position =
            Assert.Single(
                positions);

        Assert.Equal(
            "SAMP",
            position.Symbol);
        Assert.Equal(
            OrderSide.Buy,
            position.Side);
        Assert.Equal(
            10m,
            position.Quantity);
        Assert.Equal(
            1,
            exchange.GetPositionsCallCount);
        Assert.Equal(
            0,
            exchange.MutationAttemptCount);
    }

    [Fact]
    public void Existing_risk_engine_is_called_once_and_sample_fixture_is_allowed_without_mutation()
    {
        var execution =
            new TranscriptResearchRiskPreviewService()
                .Run(
                    CreatePlan(),
                    CreatePortfolio(),
                    TranscriptResearchRiskPreviewInputLoader
                        .Load(
                            SampleRiskInputPath()));

        Assert.True(
            execution.Output.Allowed);
        Assert.Empty(
            execution.Output.Reasons);
        Assert.True(
            execution.Output.RequiresRiskApproval);
        Assert.Equal(
            1,
            execution.RiskEngineCheckCount);
        Assert.Equal(
            1,
            execution.RiskSnapshotReadCount);
        Assert.Equal(
            1,
            execution.ExchangePositionReadCount);
        Assert.Equal(
            0,
            execution.ExchangeMutationAttemptCount);
        Assert.Equal(
            0,
            execution.RiskStateMutationAttemptCount);
        Assert.Equal(
            0,
            execution.RecordedRejectionCount);
    }

    [Theory]
    [InlineData("trading-disabled")]
    [InlineData("emergency-stop")]
    [InlineData("disallowed-symbol")]
    [InlineData("max-order-size")]
    [InlineData("max-position-size")]
    [InlineData("incomplete-daily-accounting")]
    public void Existing_risk_engine_rejects_expected_preview_conditions(
        string scenario)
    {
        var plan =
            CreatePlan();
        var portfolio =
            CreatePortfolio();
        var input =
            CreateRiskInput(
                tradingEnabled:
                    scenario !=
                    "trading-disabled",
                emergencyStop:
                    scenario ==
                    "emergency-stop",
                allowedSymbols:
                    scenario ==
                    "disallowed-symbol"
                        ? new[]
                        {
                            "OTHER"
                        }
                        : new[]
                        {
                            "SAMP"
                        },
                maxOrderSize:
                    scenario ==
                    "max-order-size"
                        ? 20m
                        : 50m,
                maxPositionSize:
                    scenario ==
                    "max-position-size"
                        ? 20m
                        : 50m,
                dailyNetRealizedPnL:
                    scenario ==
                    "incomplete-daily-accounting"
                        ? null
                        : 0m);

        var execution =
            new TranscriptResearchRiskPreviewService()
                .Run(
                    plan,
                    portfolio,
                    input);

        Assert.False(
            execution.Output.Allowed);
        Assert.NotEmpty(
            execution.Output.Reasons);
        Assert.Equal(
            1,
            execution.RiskEngineCheckCount);
        Assert.Equal(
            1,
            execution.RecordedRejectionCount);
        Assert.Equal(
            0,
            execution.ExchangeMutationAttemptCount);
        Assert.Equal(
            0,
            execution.RiskStateMutationAttemptCount);
    }

    [Fact]
    public void Risk_preview_cli_preserves_rebalance_audit_and_adds_allowed_preview_without_leakage()
    {
        var root =
            FindRepositoryRoot();
        using var fixture =
            new TempFixture();
        var outputPath =
            Path.Combine(
                fixture.Root,
                "risk-preview-result.json");
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
                    SampleRiskInputPath(),
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
        var plan =
            rootElement.GetProperty(
                "currentRebalancePlan");
        var preview =
            rootElement.GetProperty(
                "riskPreview");

        Assert.Equal(
            "Ready",
            plan.GetProperty("status")
                .GetString());
        Assert.Equal(
            30m,
            plan.GetProperty("orderIntent")
                .GetProperty("quantity")
                .GetDecimal());
        Assert.Equal(
            0.40m,
            rootElement.GetProperty("researchDecision")
                .GetProperty("targetWeight")
                .GetDecimal());
        Assert.Equal(
            "deterministicSyntheticState",
            preview.GetProperty("mode")
                .GetString());
        Assert.Equal(
            "SAMP",
            preview.GetProperty("symbol")
                .GetString());
        Assert.Equal(
            "Buy",
            preview.GetProperty("side")
                .GetString());
        Assert.Equal(
            30m,
            preview.GetProperty("requestedQuantity")
                .GetDecimal());
        Assert.True(
            preview.GetProperty("requiresRiskApproval")
                .GetBoolean());
        Assert.True(
            preview.GetProperty("allowed")
                .GetBoolean());
        Assert.Empty(
            preview.GetProperty("reasons")
                .EnumerateArray());

        var json =
            File.ReadAllText(
                outputPath);

        Assert.DoesNotContain(
            "\"segments\"",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "apiKey",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "OPENAI_API_KEY",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GROQ_API_KEY",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Owned_preview_code_has_no_execution_or_ibkr_bridge()
    {
        var root =
            FindRepositoryRoot();
        var previewSource =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.TranscriptResearchDemo",
                    "TranscriptResearchRiskPreview.cs"));
        var providerSource =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.ProviderTranscriptResearchDemo",
                    "ProviderTranscriptResearchDemo.cs"));

        Assert.Contains(
            "new RiskEngine(",
            previewSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".CheckAsync(",
            previewSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OrderManager",
            previewSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SignalExecutionService",
            previewSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IbkrPaperExchangeClient",
            previewSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Guid.NewGuid",
            previewSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "#pragma",
            providerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "rollForward is not",
            providerSource,
            StringComparison.Ordinal);
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
        bool tradingEnabled = true,
        bool emergencyStop = false,
        IReadOnlyCollection<string>? allowedSymbols = null,
        decimal maxOrderSize = 50m,
        decimal maxPositionSize = 50m,
        decimal? dailyNetRealizedPnL = 0m)
    {
        var settings =
            new RiskSettings
            {
                MaxPositionSize =
                    maxPositionSize,
                MaxOrderSize =
                    maxOrderSize,
                MaxDailyLoss =
                    1000m,
                MaxOpenPositions =
                    5,
                AllowedSymbols =
                    new HashSet<string>(
                        allowedSymbols
                        ?? new[]
                        {
                            "SAMP"
                        },
                        StringComparer.OrdinalIgnoreCase)
            };
        var snapshot =
            new RiskControlSnapshot(
                tradingEnabled,
                emergencyStop,
                emergencyStop
                    ? "synthetic test stop"
                    : null,
                "USD",
                dailyNetRealizedPnL ?? 0m,
                0m,
                dailyNetRealizedPnL,
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

    private static string SampleRiskInputPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "research",
            "provider-transcript-demo",
            "risk-preview-input.json");

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

    private sealed class TempFixture :
        IDisposable
    {
        public TempFixture()
        {
            Root =
                Path.Combine(
                    Path.GetTempPath(),
                    "tradeops-vs16-" +
                    Guid.NewGuid()
                        .ToString("N"));
            Directory.CreateDirectory(
                Root);
        }

        public string Root
        {
            get;
        }

        public string MutateRiskInput(
            Action<JsonObject> mutation)
        {
            var root =
                JsonNode.Parse(
                    File.ReadAllText(
                        SampleRiskInputPath()))!
                    .AsObject();

            mutation(
                root);

            var path =
                Path.Combine(
                    Root,
                    Guid.NewGuid()
                        .ToString("N") +
                    ".json");

            File.WriteAllText(
                path,
                root.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(
                    Root))
            {
                Directory.Delete(
                    Root,
                    recursive:
                        true);
            }
        }
    }

    private sealed class ProviderFixture :
        IDisposable
    {
        public ProviderFixture()
        {
            Root =
                Path.Combine(
                    Path.GetTempPath(),
                    "tradeops-vs16-provider-" +
                    Guid.NewGuid()
                        .ToString("N"));
            TradeOpsRoot =
                Path.Combine(
                    Root,
                    "TradeOps");
            DocFlowRoot =
                Path.Combine(
                    Root,
                    "DocFlow");
            WorkDirectory =
                Path.Combine(
                    Root,
                    "work");

            Directory.CreateDirectory(
                TradeOpsRoot);
            File.WriteAllText(
                Path.Combine(
                    TradeOpsRoot,
                    "global.json"),
                """
                {
                  "sdk": {
                    "version": "8.0.400",
                    "rollForward": "latestPatch",
                    "allowPrerelease": false
                  }
                }
                """);

            RebalanceInput =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "rebalance-input.json");
            RiskInput =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "risk-preview-input.json");
            CreateTradeOpsFile(
                "samples",
                "research",
                "provider-transcript-demo",
                "prior-raw.json");
            CreateTradeOpsFile(
                "samples",
                "research",
                "provider-transcript-demo",
                "current-raw.json");
            CreateTradeOpsFile(
                "samples",
                "research",
                "provider-transcript-demo",
                "consumer-manifest.json");
            CreateTradeOpsFile(
                "samples",
                "research",
                "transcript-research",
                "policy.json");
            CreateTradeOpsFile(
                "schemas",
                "research",
                "earnings-transcript-facts-v1.schema-request.json");

            var worker =
                Path.Combine(
                    DocFlowRoot,
                    "src",
                    "DocFlow.Extraction.Worker");
            Directory.CreateDirectory(
                worker);
            File.WriteAllText(
                Path.Combine(
                    worker,
                    "text_artifact_main.py"),
                string.Empty);
        }

        public string Root { get; }
        public string TradeOpsRoot { get; }
        public string DocFlowRoot { get; }
        public string WorkDirectory { get; }
        public string RebalanceInput { get; }
        public string RiskInput { get; }

        public ProviderTranscriptResearchDemoOptions Options(
            string riskInput,
            bool executionDryRun = false) =>
            new(
                DocFlowRoot,
                "explicit-model",
                "python",
                WorkDirectory,
                ProviderTranscriptResearchProvider.OpenAi,
                null,
                RebalanceInput,
                riskInput,
                executionDryRun);

        private string CreateTradeOpsFile(
            params string[] segments)
        {
            var path =
                Path.Combine(
                    new[]
                    {
                        TradeOpsRoot
                    }
                    .Concat(
                        segments)
                    .ToArray());
            var directory =
                Path.GetDirectoryName(
                    path)!;

            Directory.CreateDirectory(
                directory);
            File.WriteAllText(
                path,
                "{}");

            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(
                    Root))
            {
                Directory.Delete(
                    Root,
                    recursive:
                        true);
            }
        }
    }

    private sealed class ApiKeyEnvironmentReader :
        IEnvironmentReader
    {
        public string? Get(
            string name) =>
            string.Equals(
                name,
                ProviderTranscriptResearchDemoHarness
                    .OpenAiApiKeyEnvironmentVariable,
                StringComparison.Ordinal)
                ? "synthetic-provider-key"
                : null;
    }

    private sealed class RecordingProcessRunner :
        IChildProcessRunner
    {
        public List<ChildProcessInvocation> PreflightInvocations
        {
            get;
        } =
            new();

        public List<ChildProcessInvocation> Invocations
        {
            get;
        } =
            new();

        public ChildProcessResult Run(
            ChildProcessInvocation invocation)
        {
            if (invocation.Arguments.Count == 1
                && string.Equals(
                    invocation.Arguments[0],
                    "--version",
                    StringComparison.Ordinal))
            {
                PreflightInvocations.Add(
                    invocation);

                return new ChildProcessResult(
                    0,
                    "8.0.421\n",
                    string.Empty);
            }

            Invocations.Add(
                invocation);

            if (invocation.Arguments.Contains(
                    "text_artifact_main.py"))
            {
                WriteArgumentFile(
                    invocation,
                    "--output-normalized-json");
                WriteArgumentFile(
                    invocation,
                    "--output-structured-json");
            }
            else
            {
                WriteArgumentFile(
                    invocation,
                    "--json");
            }

            return new ChildProcessResult(
                0,
                string.Empty,
                string.Empty);
        }

        private static void WriteArgumentFile(
            ChildProcessInvocation invocation,
            string option)
        {
            var index =
                invocation.Arguments
                    .ToList()
                    .IndexOf(
                        option);

            Assert.True(
                index >= 0
                && index + 1 <
                invocation.Arguments.Count);

            var path =
                invocation.Arguments[
                    index + 1];
            var directory =
                Path.GetDirectoryName(
                    path);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            File.WriteAllText(
                path,
                "{}");
        }
    }
}
