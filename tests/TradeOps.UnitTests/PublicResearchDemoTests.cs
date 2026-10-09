using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.PublicResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class PublicResearchDemoTests
{
    [Fact]
    public void Sec_submissions_parser_preserves_exact_metadata()
    {
        var filings =
            SecSubmissionParser.Parse(
                SubmissionsJson);

        Assert.Equal(
            2,
            filings.Count);

        var latest =
            filings[^1];

        Assert.Equal(
            "0000051143",
            latest.Cik);
        Assert.Equal(
            "0000051143-26-000020",
            latest.AccessionNumber);
        Assert.Equal(
            "10-Q",
            latest.FormType);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                22,
                20,
                10,
                0,
                TimeSpan.Zero),
            latest.AcceptedAt);
        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/51143/000005114326000020/ibm-20260630.htm",
            latest.SourceUri.AbsoluteUri);
    }

    [Fact]
    public void Companyfacts_parser_uses_exact_accession_and_quarter_interval()
    {
        var filings =
            SecSubmissionParser.Parse(
                SubmissionsJson);

        var structured =
            SecCompanyFactsParser
                .CreateStructuredFilings(
                    CompanyFactsJson,
                    filings,
                    "IBM");

        var latest =
            structured[^1];
        var normalized =
            SecStructuredFilingNormalizer
                .Normalize(latest);

        Assert.Equal(
            "0000051143-26-000020",
            latest.AccessionNumber);
        Assert.Equal(
            new DateOnly(
                2026,
                4,
                1),
            latest.FiscalPeriodStart);
        Assert.Equal(
            new DateOnly(
                2026,
                6,
                30),
            latest.FiscalPeriodEnd);
        Assert.Equal(
            "FY2026-Q2",
            latest.FiscalPeriod);
        Assert.Equal(
            110m,
            normalized.Revenue);
        Assert.Equal(
            2.20m,
            normalized.DilutedEps);
        Assert.DoesNotContain(
            latest.Facts,
            fact =>
                fact.Value ==
                999m);
    }

    [Fact]
    public void Sec_projection_separates_source_availability_and_actual_retrieval()
    {
        var filingMetadata =
            SecSubmissionParser.Parse(
                SubmissionsJson)[^1];
        var filing =
            SecCompanyFactsParser
                .CreateStructuredFilings(
                    CompanyFactsJson,
                    SecSubmissionParser.Parse(
                        SubmissionsJson),
                    "IBM")[^1];
        var actualRetrievedAt =
            new DateTimeOffset(
                2026,
                10,
                7,
                8,
                0,
                0,
                TimeSpan.Zero);
        var expectedAvailability =
            new DateTimeOffset(
                2026,
                7,
                23,
                2,
                0,
                0,
                TimeSpan.Zero);

        Assert.Equal(
            expectedAvailability,
            SecHistoricalAvailabilityPolicy
                .Resolve(filingMetadata));
        Assert.Equal(
            expectedAvailability,
            filing.PubliclyAvailableAt);
        Assert.NotEqual(
            filing.AcceptedAt,
            filing.PubliclyAvailableAt);

        var facts =
            SecStructuredFilingNormalizer
                .Normalize(filing);
        var earningsEvent =
            SecEarningsEventFactory.Create(
                facts,
                actualRetrievedAt);

        Assert.Equal(
            filing.AcceptedAt,
            earningsEvent.Provenance.SourceTimestamp);
        Assert.Equal(
            expectedAvailability,
            earningsEvent.PublishedAt);
        Assert.Equal(
            actualRetrievedAt,
            earningsEvent.Provenance.RetrievedAt);
        Assert.True(
            earningsEvent.Provenance.SourceTimestamp <=
            earningsEvent.PublishedAt);
        Assert.True(
            earningsEvent.PublishedAt <=
            earningsEvent.Provenance.RetrievedAt);
        Assert.Equal(
            filing.AccessionNumber,
            earningsEvent.Provenance.SourceDocumentId);
        Assert.Equal(
            "CIK:0000051143",
            earningsEvent.Provenance.IssuerId);
        Assert.Equal(
            filing.SourceUri,
            earningsEvent.Provenance.SourceUri);
    }

    [Fact]
    public void Alpha_vantage_daily_parser_uses_New_York_DST()
    {
        const string json =
            """
            {
              "Time Series (Daily)": {
                "2026-01-15": {
                  "1. open": "100",
                  "2. high": "101",
                  "3. low": "99",
                  "4. close": "100.5"
                },
                "2026-07-15": {
                  "1. open": "120",
                  "2. high": "121",
                  "3. low": "119",
                  "4. close": "120.5"
                }
              }
            }
            """;

        var bars =
            AlphaVantageDailyParser.Parse(
                json,
                new(
                    "IBM",
                    AssetClass.Stock,
                    "USD"));

        Assert.Equal(
            new DateTimeOffset(
                2026,
                1,
                15,
                14,
                30,
                0,
                TimeSpan.Zero),
            bars[0].OpenTime);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                1,
                15,
                21,
                0,
                0,
                TimeSpan.Zero),
            bars[0].CloseTime);

        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                15,
                13,
                30,
                0,
                TimeSpan.Zero),
            bars[1].OpenTime);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                15,
                20,
                0,
                0,
                TimeSpan.Zero),
            bars[1].CloseTime);
    }

    [Fact]
    public void Cache_roundtrip_verifies_manifest_and_payload_checksum()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-public-demo-{Guid.NewGuid():N}");

        try
        {
            var snapshot =
                new PublicDemoRawSnapshot(
                    new DateTimeOffset(
                        2026,
                        10,
                        7,
                        8,
                        0,
                        0,
                        TimeSpan.Zero),
                    SubmissionsJson,
                    CompanyFactsJson,
                    MarketJson);

            var manifest =
                PublicDemoCache.Save(
                    root,
                    snapshot,
                    new[]
                    {
                        "0000051143-26-000010",
                        "0000051143-26-000020"
                    },
                    new DateOnly(
                        2026,
                        7,
                        21),
                    new DateOnly(
                        2026,
                        10,
                        1));

            var loaded =
                PublicDemoCache.Load(root);

            Assert.Equal(
                snapshot.RetrievedAt,
                loaded.Manifest.RetrievedAt);
            Assert.Equal(
                3,
                manifest.Resources.Count);
            Assert.All(
                manifest.Resources,
                item =>
                    Assert.Equal(
                        64,
                        item.Sha256.Length));

            File.AppendAllText(
                Path.Combine(
                    root,
                    "alpha-vantage-ibm-daily.json"),
                " ");

            Assert.Throws<InvalidOperationException>(
                () =>
                    PublicDemoCache.Load(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Full_composition_invokes_existing_VS01_pipeline()
    {
        var snapshotRetrievedAt =
            new DateTimeOffset(
                2026,
                10,
                7,
                8,
                0,
                0,
                TimeSpan.Zero);
        var composition =
            PublicResearchDemoComposer.Compose(
                new PublicDemoRawSnapshot(
                    snapshotRetrievedAt,
                    SubmissionsJson,
                    CompanyFactsJson,
                    MarketJson),
                CreatePolicy());

        var result =
            composition.Result;

        Assert.Equal(
            "IBM",
            result.Instrument.Symbol);
        Assert.Equal(
            "sec:IBM:0000051143-26-000020",
            result.EventId);
        Assert.Equal(
            "FY2026-Q2",
            result.FiscalPeriod);
        Assert.Equal(
            EarningsAssessment.Positive,
            result.Assessment);
        Assert.Equal(
            0.40m,
            result.TargetWeight);
        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            result.LatestDecision.Action);
        Assert.Equal(
            result.PublishedAt,
            result.LatestDecision.GeneratedAt);
        Assert.Equal(
            snapshotRetrievedAt,
            composition.EarningsEvents[^1]
                .Provenance.RetrievedAt);
        Assert.Equal(
            1,
            result.EventCount);
        Assert.Single(
            result.Backtest.Plans);
        Assert.Equal(
            RebalancePlanStatus.Ready,
            result.CurrentRebalancePlan.Status);
        Assert.NotNull(
            result.CurrentRebalancePlan.OrderIntent);
        Assert.True(
            result.CurrentRebalancePlan
                .OrderIntent!
                .RequiresRiskApproval);
    }

    [Fact]
    public void Historical_after_hours_availability_executes_only_at_next_daily_open()
    {
        var composition =
            PublicResearchDemoComposer.Compose(
                new PublicDemoRawSnapshot(
                    new DateTimeOffset(
                        2026,
                        10,
                        7,
                        8,
                        0,
                        0,
                        TimeSpan.Zero),
                    SubmissionsJson,
                    CompanyFactsJson,
                    MarketJson),
                CreatePolicy());

        var latestEvent =
            composition.EarningsEvents[^1];
        var decision =
            composition.Result.LatestDecision;
        var fill =
            Assert.Single(
                composition.Result.Backtest.Fills);

        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                23,
                2,
                0,
                0,
                TimeSpan.Zero),
            latestEvent.PublishedAt);
        Assert.Equal(
            latestEvent.PublishedAt,
            decision.GeneratedAt);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                23,
                13,
                30,
                0,
                TimeSpan.Zero),
            fill.ExecutedAt);
        Assert.True(
            fill.ExecutedAt >
            decision.GeneratedAt);
        Assert.DoesNotContain(
            composition.Result.Backtest.Fills,
            item =>
                item.ExecutedAt <=
                decision.GeneratedAt);
    }

    [Fact]
    public void Sample_policy_file_loads_and_maps_to_existing_pipeline_types()
    {
        var path =
            Path.Combine(
                FindRepositoryRoot(),
                "samples",
                "research",
                "earnings-policy.sample.json");

        var loaded =
            PublicResearchDemoCli.LoadPolicyFromFile(
                path);

        Assert.True(
            loaded.IsValid);
        Assert.NotNull(
            loaded.Policy);
        Assert.Equal(
            1,
            loaded.Policy.SchemaVersion);
        Assert.Equal(
            "sample-earnings-policy-v1",
            loaded.Policy.StrategyId);
        Assert.Equal(
            64,
            loaded.Policy.Fingerprint.Length);
        Assert.Equal(
            0.05m,
            loaded.Policy.DecisionRuleSettings
                .RevenueGrowthThreshold);
        Assert.Equal(
            0.40m,
            loaded.Policy.TargetWeightPolicy
                .PositiveTargetWeight);
    }

    [Fact]
    public void Missing_policy_file_is_structured()
    {
        var path =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-missing-policy-{Guid.NewGuid():N}.json");

        var loaded =
            PublicResearchDemoCli.LoadPolicyFromFile(
                path);

        Assert.False(
            loaded.IsValid);
        var error =
            Assert.Single(
                loaded.Errors);
        Assert.Equal(
            "policy_file_not_found",
            error.Code);
        Assert.Equal(
            "$.policy",
            error.Path);

        var formatted =
            PublicResearchDemoCli
                .FormatPolicyValidationFailure(
                    loaded.Errors);

        Assert.Contains(
            "POLICY VALIDATION: FAIL",
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            "code=policy_file_not_found",
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            "path=$.policy",
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            "message=",
            formatted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_policy_json_is_structured()
    {
        var path =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-malformed-policy-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                path,
                "{ \"schemaVersion\": 1 ");

            var loaded =
                PublicResearchDemoCli.LoadPolicyFromFile(
                    path);

            Assert.False(
                loaded.IsValid);
            var error =
                Assert.Single(
                    loaded.Errors);
            Assert.Equal(
                EarningsResearchPolicyValidationCodes
                    .InvalidJson,
                error.Code);
            Assert.Equal(
                "$",
                error.Path);
            Assert.DoesNotContain(
                "JsonException",
                PublicResearchDemoCli
                    .FormatPolicyValidationFailure(
                        loaded.Errors),
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_policy_prevents_cache_mutation_and_composition()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-invalid-policy-{Guid.NewGuid():N}");
        var policyPath =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-invalid-policy-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                policyPath,
                ValidPolicyJson.Replace(
                    "\"schemaVersion\": 1",
                    "\"schemaVersion\": 2",
                    StringComparison.Ordinal));

            var exitCode =
                await PublicResearchDemoCli.RunAsync(
                    new[]
                    {
                        "--policy",
                        policyPath,
                        "--cache",
                        root
                    });

            Assert.NotEqual(
                0,
                exitCode);
            Assert.False(
                Directory.Exists(root));
        }
        finally
        {
            File.Delete(policyPath);

            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Strategy_id_and_fingerprint_flow_to_output()
    {
        var policy =
            CreatePolicy();
        var composition =
            PublicResearchDemoComposer.Compose(
                CreateSnapshot(),
                policy);
        var output =
            PublicResearchDemoComposer.ToOutput(
                composition);

        Assert.Equal(
            policy.StrategyId,
            composition.Result.LatestDecision
                .StrategyId);
        Assert.Equal(
            policy.Fingerprint,
            output.Policy.Fingerprint);
        Assert.Equal(
            policy.StrategyId,
            output.Policy.StrategyId);
        Assert.Equal(
            policy.SchemaVersion,
            output.Policy.SchemaVersion);
    }

    [Fact]
    public void Same_logical_policy_produces_same_run_identity_and_results()
    {
        const string reordered =
            """
            {"targetWeights":{"negative":0,"positive":0.4000,"neutral":0.2},"thresholds":{"minimumDirectionalSignals":2,"operatingMarginDelta":0.0100,"dilutedEpsGrowth":0.050,"revenueGrowth":0.05},"strategyId":"sample-earnings-policy-v1","schemaVersion":1}
            """;

        var firstPolicy =
            CreatePolicy(
                ValidPolicyJson);
        var secondPolicy =
            CreatePolicy(
                reordered);
        var snapshot =
            CreateSnapshot();

        var first =
            PublicResearchDemoComposer.Compose(
                snapshot,
                firstPolicy);
        var second =
            PublicResearchDemoComposer.Compose(
                snapshot,
                secondPolicy);

        Assert.Equal(
            firstPolicy.Fingerprint,
            secondPolicy.Fingerprint);
        Assert.Equal(
            first.Result.LatestDecision.DecisionId,
            second.Result.LatestDecision.DecisionId);
        Assert.Equal(
            first.Result.Assessment,
            second.Result.Assessment);
        Assert.Equal(
            first.Result.TargetWeight,
            second.Result.TargetWeight);
        Assert.Equal(
            first.Result.TotalReturn,
            second.Result.TotalReturn);
        Assert.Equal(
            first.Result.RebalanceQuantity,
            second.Result.RebalanceQuantity);
    }

    [Fact]
    public void Different_valid_target_weight_policy_changes_decision_and_rebalance()
    {
        var firstPolicy =
            CreatePolicy(
                ValidPolicyJson);
        var secondPolicy =
            CreatePolicy(
                ValidPolicyJson.Replace(
                    "\"positive\": 0.40",
                    "\"positive\": 0.20",
                    StringComparison.Ordinal));
        var snapshot =
            CreateSnapshot();

        var first =
            PublicResearchDemoComposer.Compose(
                snapshot,
                firstPolicy);
        var second =
            PublicResearchDemoComposer.Compose(
                snapshot,
                secondPolicy);

        Assert.NotEqual(
            firstPolicy.Fingerprint,
            secondPolicy.Fingerprint);
        Assert.NotEqual(
            first.Result.LatestDecision.DecisionId,
            second.Result.LatestDecision.DecisionId);
        Assert.Equal(
            0.40m,
            first.Result.TargetWeight);
        Assert.Equal(
            0.20m,
            second.Result.TargetWeight);
        Assert.NotEqual(
            first.Result.RebalanceQuantity,
            second.Result.RebalanceQuantity);
        Assert.NotEqual(
            first.Result.RebalanceNotional,
            second.Result.RebalanceNotional);
    }

    [Fact]
    public async Task Fingerprint_is_written_to_machine_readable_json_artifact()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                $"tradeops-policy-artifact-{Guid.NewGuid():N}");
        var jsonPath =
            Path.Combine(
                root,
                "result.json");
        var policyPath =
            Path.Combine(
                FindRepositoryRoot(),
                "samples",
                "research",
                "earnings-policy.sample.json");

        try
        {
            PublicDemoCache.Save(
                root,
                CreateSnapshot(),
                new[]
                {
                    "0000051143-26-000010",
                    "0000051143-26-000020"
                },
                new DateOnly(
                    2026,
                    7,
                    21),
                new DateOnly(
                    2026,
                    10,
                    1));

            var loadedPolicy =
                PublicResearchDemoCli
                    .LoadPolicyFromFile(
                        policyPath);

            var exitCode =
                await PublicResearchDemoCli.RunAsync(
                    new[]
                    {
                        "--policy",
                        policyPath,
                        "--cache",
                        root,
                        "--json",
                        jsonPath
                    });

            Assert.Equal(
                0,
                exitCode);
            Assert.True(
                File.Exists(jsonPath));

            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(
                        jsonPath));

            Assert.Equal(
                loadedPolicy.Policy!.Fingerprint,
                document.RootElement
                    .GetProperty("policy")
                    .GetProperty("fingerprint")
                    .GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Network_gate_fails_closed_without_explicit_confirmation()
    {
        var oldConfirmation =
            Environment.GetEnvironmentVariable(
                PublicDemoNetworkGate
                    .ConfirmationVariable);
        var oldUserAgent =
            Environment.GetEnvironmentVariable(
                PublicDemoNetworkGate
                    .SecUserAgentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                PublicDemoNetworkGate
                    .ConfirmationVariable,
                null);
            Environment.SetEnvironmentVariable(
                PublicDemoNetworkGate
                    .SecUserAgentVariable,
                null);

            Assert.Throws<InvalidOperationException>(
                PublicDemoNetworkGate
                    .RequireExternalFetch);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                PublicDemoNetworkGate
                    .ConfirmationVariable,
                oldConfirmation);
            Environment.SetEnvironmentVariable(
                PublicDemoNetworkGate
                    .SecUserAgentVariable,
                oldUserAgent);
        }
    }

    private static PublicDemoRawSnapshot CreateSnapshot() =>
        new(
            new DateTimeOffset(
                2026,
                10,
                7,
                8,
                0,
                0,
                TimeSpan.Zero),
            SubmissionsJson,
            CompanyFactsJson,
            MarketJson);

    private static PublicResearchDemoPolicy CreatePolicy(
        string json = ValidPolicyJson)
    {
        var loaded =
            EarningsResearchPolicyConfiguration.Load(
                json);

        Assert.True(
            loaded.IsValid);
        Assert.NotNull(
            loaded.Definition);
        Assert.NotNull(
            loaded.Fingerprint);

        return new PublicResearchDemoPolicy(
            loaded.Definition.SchemaVersion,
            loaded.Definition.StrategyId,
            loaded.Fingerprint,
            EarningsResearchPolicyConfiguration
                .ToDecisionRuleSettings(
                    loaded.Definition),
            EarningsResearchPolicyConfiguration
                .ToTargetWeightPolicy(
                    loaded.Definition));
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

        throw new InvalidOperationException(
            "TradeOps repository root was not found.");
    }

    private const string ValidPolicyJson =
        """
        {
          "schemaVersion": 1,
          "strategyId": "sample-earnings-policy-v1",
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
        """;

    private const string SubmissionsJson =
        """
        {
          "cik": "51143",
          "tickers": ["IBM"],
          "filings": {
            "recent": {
              "accessionNumber": [
                "0000051143-26-000020",
                "0000051143-26-000010"
              ],
              "form": ["10-Q", "10-Q"],
              "reportDate": ["2026-06-30", "2026-03-31"],
              "filingDate": ["2026-07-22", "2026-04-22"],
              "acceptanceDateTime": [
                "2026-07-22T20:10:00Z",
                "2026-04-22T20:10:00Z"
              ],
              "primaryDocument": [
                "ibm-20260630.htm",
                "ibm-20260331.htm"
              ]
            }
          }
        }
        """;

    private const string CompanyFactsJson =
        """
        {
          "cik": 51143,
          "entityName": "International Business Machines Corporation",
          "facts": {
            "us-gaap": {
              "RevenueFromContractWithCustomerExcludingAssessedTax": {
                "units": {
                  "USD": [
                    {
                      "start": "2026-01-01",
                      "end": "2026-03-31",
                      "val": 100,
                      "accn": "0000051143-26-000010",
                      "fy": 2026,
                      "fp": "Q1",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-01-01",
                      "end": "2026-06-30",
                      "val": 205,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 110,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 999,
                      "accn": "0000051143-26-999999",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    }
                  ]
                }
              },
              "EarningsPerShareDiluted": {
                "units": {
                  "USD/shares": [
                    {
                      "start": "2026-01-01",
                      "end": "2026-03-31",
                      "val": 2.00,
                      "accn": "0000051143-26-000010",
                      "fy": 2026,
                      "fp": "Q1",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 2.20,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    }
                  ]
                }
              },
              "NetIncomeLoss": {
                "units": {
                  "USD": [
                    {
                      "start": "2026-01-01",
                      "end": "2026-03-31",
                      "val": 10,
                      "accn": "0000051143-26-000010",
                      "fy": 2026,
                      "fp": "Q1",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 12,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    }
                  ]
                }
              },
              "GrossProfit": {
                "units": {
                  "USD": [
                    {
                      "start": "2026-01-01",
                      "end": "2026-03-31",
                      "val": 50,
                      "accn": "0000051143-26-000010",
                      "fy": 2026,
                      "fp": "Q1",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 56,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    }
                  ]
                }
              },
              "OperatingIncomeLoss": {
                "units": {
                  "USD": [
                    {
                      "start": "2026-01-01",
                      "end": "2026-03-31",
                      "val": 20,
                      "accn": "0000051143-26-000010",
                      "fy": 2026,
                      "fp": "Q1",
                      "form": "10-Q"
                    },
                    {
                      "start": "2026-04-01",
                      "end": "2026-06-30",
                      "val": 24,
                      "accn": "0000051143-26-000020",
                      "fy": 2026,
                      "fp": "Q2",
                      "form": "10-Q"
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string MarketJson =
        """
        {
          "Meta Data": {
            "2. Symbol": "IBM"
          },
          "Time Series (Daily)": {
            "2026-10-01": {
              "1. open": "129",
              "2. high": "131",
              "3. low": "128",
              "4. close": "130"
            },
            "2026-07-24": {
              "1. open": "124",
              "2. high": "126",
              "3. low": "123",
              "4. close": "125"
            },
            "2026-07-23": {
              "1. open": "120",
              "2. high": "125",
              "3. low": "119",
              "4. close": "124"
            },
            "2026-07-22": {
              "1. open": "118",
              "2. high": "121",
              "3. low": "117",
              "4. close": "120"
            },
            "2026-07-21": {
              "1. open": "116",
              "2. high": "119",
              "3. low": "115",
              "4. close": "118"
            }
          }
        }
        """;
}
