using System.Text.Json;
using System.Text.Json.Nodes;
using TradeOps.TranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ProviderTranscriptResearchRebalanceEndToEndTests
{
    [Fact]
    public void Research_only_cli_remains_backward_compatible()
    {
        using var fixture = new Fixture();

        var result =
            RunCli(
                fixture,
                rebalanceInput: null);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.Contains(
            "TRANSCRIPT RESEARCH DEMO: PASS",
            result.Output,
            StringComparison.Ordinal);

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(
                    fixture.OutputPath));

        Assert.True(
            document.RootElement.TryGetProperty(
                "researchDecision",
                out _));
        Assert.False(
            document.RootElement.TryGetProperty(
                "backtestMetrics",
                out _));
    }

    [Fact]
    public void Explicit_vs13_mode_emits_existing_decision_backtest_plan_and_order_intent()
    {
        using var fixture = new Fixture();

        var result =
            RunCli(
                fixture,
                fixture.RebalanceInput);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.Contains(
            "TRANSCRIPT RESEARCH REBALANCE DEMO: PASS",
            result.Output,
            StringComparison.Ordinal);

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(
                    fixture.OutputPath));
        var root =
            document.RootElement;

        Assert.Equal(
            1,
            root.GetProperty("schemaVersion")
                .GetInt32());
        Assert.Equal(
            "SAMP",
            root.GetProperty("instrument")
                .GetProperty("symbol")
                .GetString());

        var decision =
            root.GetProperty(
                "researchDecision");

        Assert.Equal(
            "SetTargetWeight",
            decision.GetProperty("action")
                .GetString());
        Assert.Equal(
            0.40m,
            decision.GetProperty("targetWeight")
                .GetDecimal());
        Assert.Equal(
            1m,
            decision.GetProperty("confidence")
                .GetDecimal());

        var metrics =
            root.GetProperty(
                "backtestMetrics");

        Assert.Equal(
            1,
            metrics.GetProperty("eventCount")
                .GetInt32());

        Assert.True(
            root.GetProperty(
                    "finalBacktestPortfolio")
                .GetProperty("netAssetValue")
                .GetDecimal() >
            0m);

        var plan =
            root.GetProperty(
                "currentRebalancePlan");

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
            "sample-earnings-2026-q1",
            root.GetProperty("prior")
                .GetProperty("eventId")
                .GetString());
        Assert.Equal(
            "FY2026-Q2",
            root.GetProperty("current")
                .GetProperty("fiscalPeriod")
                .GetString());

        Assert.False(
            string.IsNullOrWhiteSpace(
                root.GetProperty("prior")
                    .GetProperty("docFlowFingerprint")
                    .GetString()));
        Assert.False(
            string.IsNullOrWhiteSpace(
                root.GetProperty("current")
                    .GetProperty("extractionEngine")
                    .GetString()));
    }

    [Fact]
    public void Explicit_vs13_mode_is_deterministic()
    {
        using var fixture = new Fixture();

        var first =
            RunCli(
                fixture,
                fixture.RebalanceInput);

        Assert.Equal(
            0,
            first.ExitCode);

        var firstJson =
            File.ReadAllText(
                fixture.OutputPath);
        var second =
            RunCli(
                fixture,
                fixture.RebalanceInput);

        Assert.Equal(
            0,
            second.ExitCode);
        Assert.Equal(
            firstJson,
            File.ReadAllText(
                fixture.OutputPath));
    }

    [Fact]
    public void Strict_input_rejects_duplicate_target_weight_contract()
    {
        using var fixture = new Fixture();
        var mutated =
            fixture.MutateInput(
                root =>
                    root["targetWeight"] =
                        0.99m);

        var result =
            RunCli(
                fixture,
                mutated);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Contains(
            "strict VS-13 demo schema",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Instrument_mismatch_fails_closed()
    {
        using var fixture = new Fixture();
        var mutated =
            fixture.MutateInput(
                root =>
                    root["instrument"]![
                        "symbol"] =
                        "OTHER");

        var result =
            RunCli(
                fixture,
                mutated);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Contains(
            "exactly match",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_bars_fail_through_existing_backtester_boundary()
    {
        using var fixture = new Fixture();
        var mutated =
            fixture.MutateInput(
                root =>
                    root["historicalDailyMarketBars"] =
                        new JsonArray());

        var result =
            RunCli(
                fixture,
                mutated);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Contains(
            "At least one market-data bar is required",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_reference_price_fails_through_existing_rebalance_boundary()
    {
        using var fixture = new Fixture();
        var mutated =
            fixture.MutateInput(
                root =>
                    root["currentReferencePrice"] =
                        0m);

        var result =
            RunCli(
                fixture,
                mutated);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Contains(
            "Current reference price must be greater than zero",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_json_excludes_raw_transcript_provider_payload_and_secrets()
    {
        using var fixture = new Fixture();

        var result =
            RunCli(
                fixture,
                fixture.RebalanceInput);

        Assert.Equal(
            0,
            result.ExitCode);

        var json =
            File.ReadAllText(
                fixture.OutputPath);

        Assert.DoesNotContain(
            "\"segments\"",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Revenue for the quarter was 100 million dollars.",
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
    public void Owned_cli_code_uses_vs11_service_without_direct_engine_or_planner_calls()
    {
        var root =
            FindRepositoryRoot();
        var rebalanceSource =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.TranscriptResearchDemo",
                    "TranscriptResearchRebalanceDemo.cs"));
        var providerSource =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.ProviderTranscriptResearchDemo",
                    "ProviderTranscriptResearchDemo.cs"));

        Assert.Contains(
            "TranscriptResearchToRebalanceDemoService",
            rebalanceSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EventDrivenBacktester",
            rebalanceSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PortfolioRebalancePlanner",
            rebalanceSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EventDrivenBacktester",
            providerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PortfolioRebalancePlanner",
            providerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_input_has_no_target_weight_and_runtime_area_is_ignored()
    {
        var root =
            FindRepositoryRoot();
        var sample =
            JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        root,
                        "samples",
                        "research",
                        "provider-transcript-demo",
                        "rebalance-input.json")))!
                .AsObject();

        Assert.False(
            sample.ContainsKey(
                "targetWeight"));
        Assert.True(
            sample[
                "currentRebalanceConstraints"]!
                .AsObject()
                .ContainsKey(
                    "maxTargetWeight"));

        var gitignore =
            File.ReadAllText(
                Path.Combine(
                    root,
                    ".gitignore"));

        Assert.Contains(
            ".tradeops/",
            gitignore,
            StringComparison.Ordinal);
    }

    private static RunResult RunCli(
        Fixture fixture,
        string? rebalanceInput)
    {
        using var output =
            new StringWriter();
        using var error =
            new StringWriter();

        var args =
            new List<string>
            {
                "--manifest",
                fixture.Manifest,
                "--policy",
                fixture.Policy,
                "--json",
                fixture.OutputPath
            };

        if (rebalanceInput is not null)
        {
            args.Add(
                "--rebalance-input");
            args.Add(
                rebalanceInput);
        }

        var exitCode =
            TranscriptResearchDemoCli.Run(
                args.ToArray(),
                output,
                error);

        return new RunResult(
            exitCode,
            output.ToString(),
            error.ToString());
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

    private sealed class Fixture :
        IDisposable
    {
        public Fixture()
        {
            var root =
                FindRepositoryRoot();
            Temp =
                Path.Combine(
                    Path.GetTempPath(),
                    "tradeops-vs13-e2e-" +
                    Guid.NewGuid()
                        .ToString("N"));
            Directory.CreateDirectory(
                Temp);
            Manifest =
                Path.Combine(
                    root,
                    "samples",
                    "research",
                    "transcript-research",
                    "manifest.json");
            Policy =
                Path.Combine(
                    root,
                    "samples",
                    "research",
                    "transcript-research",
                    "policy.json");
            RebalanceInput =
                Path.Combine(
                    root,
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "rebalance-input.json");
            OutputPath =
                Path.Combine(
                    Temp,
                    "result.json");
        }

        public string Temp { get; }
        public string Manifest { get; }
        public string Policy { get; }
        public string RebalanceInput { get; }
        public string OutputPath { get; }

        public string MutateInput(
            Action<JsonObject> mutation)
        {
            var root =
                JsonNode.Parse(
                    File.ReadAllText(
                        RebalanceInput))!
                    .AsObject();

            mutation(root);

            var path =
                Path.Combine(
                    Temp,
                    "mutated-" +
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
            if (Directory.Exists(Temp))
            {
                Directory.Delete(
                    Temp,
                    recursive: true);
            }
        }
    }

    private sealed record RunResult(
        int ExitCode,
        string Output,
        string Error);
}
