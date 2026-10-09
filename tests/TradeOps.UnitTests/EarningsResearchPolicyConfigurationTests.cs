using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class EarningsResearchPolicyConfigurationTests
{
    private const string ValidJson =
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

    [Fact]
    public void Load_ValidJson_MapsDecisionRuleSettings()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson);

        Assert.True(
            result.IsValid);
        Assert.NotNull(
            result.Definition);

        var settings =
            EarningsResearchPolicyConfiguration
                .ToDecisionRuleSettings(
                    result.Definition);

        Assert.Equal(
            0.05m,
            settings.RevenueGrowthThreshold);
        Assert.Equal(
            0.05m,
            settings.DilutedEpsGrowthThreshold);
        Assert.Equal(
            0.01m,
            settings.OperatingMarginDeltaThreshold);
        Assert.Equal(
            2,
            settings.MinimumDirectionalSignals);
    }

    [Fact]
    public void Load_ValidJson_MapsTargetWeightPolicy()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson);

        Assert.True(
            result.IsValid);
        Assert.NotNull(
            result.Definition);

        var policy =
            EarningsResearchPolicyConfiguration
                .ToTargetWeightPolicy(
                    result.Definition);

        Assert.Equal(
            0.40m,
            policy.PositiveTargetWeight);
        Assert.Equal(
            0.20m,
            policy.NeutralTargetWeight);
        Assert.Equal(
            0.00m,
            policy.NegativeTargetWeight);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"schemaVersion\": 1",
                    "\"schemaVersion\": 2",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .UnsupportedSchemaVersion);
    }

    [Fact]
    public void Load_EmptyStrategyId_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"sample-earnings-policy-v1\"",
                    "\"   \"",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .InvalidStrategyId);
    }

    [Fact]
    public void Load_NegativeThreshold_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"revenueGrowth\": 0.05",
                    "\"revenueGrowth\": -0.01",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .NegativeThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Load_MinimumDirectionalSignalsOutsideRange_FailsClosed(
        int value)
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"minimumDirectionalSignals\": 2",
                    $"\"minimumDirectionalSignals\": {value}",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .InvalidMinimumDirectionalSignals);
    }

    [Fact]
    public void Load_TargetWeightBelowZero_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"positive\": 0.40",
                    "\"positive\": -0.01",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .TargetWeightOutOfRange);
    }

    [Fact]
    public void Load_TargetWeightAboveOne_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"positive\": 0.40",
                    "\"positive\": 1.01",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .TargetWeightOutOfRange);
    }

    [Fact]
    public void Load_ExcessiveTargetWeightPrecision_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"positive\": 0.40",
                    "\"positive\": 0.123456789",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .TargetWeightPrecisionExceeded);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsValidationError()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                "{ \"schemaVersion\": 1 ");

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .InvalidJson);
    }

    [Fact]
    public void Load_UnknownProperty_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"strategyId\":",
                    "\"unsupported\": true,\n  \"strategyId\":",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .UnknownProperty);
    }

    [Fact]
    public void Load_DuplicateProperty_FailsClosed()
    {
        var result =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson.Replace(
                    "\"strategyId\": \"sample-earnings-policy-v1\"",
                    "\"strategyId\": \"sample-earnings-policy-v1\",\n  \"strategyId\": \"duplicate\"",
                    StringComparison.Ordinal));

        AssertValidationError(
            result,
            EarningsResearchPolicyValidationCodes
                .DuplicateProperty);
    }

    [Fact]
    public void Fingerprint_EquivalentJsonOrderAndWhitespace_IsStable()
    {
        const string reordered =
            """
            {"targetWeights":{"negative":0,"positive":0.4000,"neutral":0.2},"thresholds":{"minimumDirectionalSignals":2,"operatingMarginDelta":0.0100,"dilutedEpsGrowth":0.050,"revenueGrowth":0.05},"strategyId":"sample-earnings-policy-v1","schemaVersion":1}
            """;

        var first =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson);
        var second =
            EarningsResearchPolicyConfiguration.Load(
                reordered);

        Assert.True(
            first.IsValid);
        Assert.True(
            second.IsValid);
        Assert.NotNull(
            first.Fingerprint);
        Assert.Equal(
            64,
            first.Fingerprint.Length);
        Assert.Equal(
            first.Fingerprint,
            second.Fingerprint);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesDefinitionAndFingerprint()
    {
        var first =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson);

        Assert.True(
            first.IsValid);
        Assert.NotNull(
            first.Definition);

        var serialized =
            EarningsResearchPolicyConfiguration.Serialize(
                first.Definition);
        var second =
            EarningsResearchPolicyConfiguration.Load(
                serialized);

        Assert.True(
            second.IsValid);
        Assert.Equal(
            first.Definition,
            second.Definition);
        Assert.Equal(
            first.Fingerprint,
            second.Fingerprint);
    }

    [Fact]
    public void Composition_LoadedPolicy_DrivesExistingDecisionRule()
    {
        var loaded =
            EarningsResearchPolicyConfiguration.Load(
                ValidJson);

        Assert.True(
            loaded.IsValid);
        Assert.NotNull(
            loaded.Definition);

        var settings =
            EarningsResearchPolicyConfiguration
                .ToDecisionRuleSettings(
                    loaded.Definition);
        var targetWeights =
            EarningsResearchPolicyConfiguration
                .ToTargetWeightPolicy(
                    loaded.Definition);

        var prior =
            CreateEvent(
                "sample-event-prior",
                new DateTimeOffset(
                    2025,
                    1,
                    10,
                    13,
                    0,
                    0,
                    TimeSpan.Zero),
                revenue:
                    100m,
                dilutedEps:
                    2m,
                operatingMargin:
                    0.10m);

        var current =
            CreateEvent(
                "sample-event-current",
                new DateTimeOffset(
                    2025,
                    4,
                    10,
                    13,
                    0,
                    0,
                    TimeSpan.Zero),
                revenue:
                    110m,
                dilutedEps:
                    2.2m,
                operatingMargin:
                    0.12m);

        var assessment =
            DeterministicEarningsDecisionRule.Assess(
                current,
                prior,
                settings);
        var decision =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                current.Provenance.RetrievedAt,
                targetWeights,
                loaded.Definition.StrategyId,
                settings);

        Assert.Equal(
            EarningsAssessment.Positive,
            assessment.Assessment);
        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            decision.Action);
        Assert.Equal(
            0.40m,
            decision.TargetWeight);
        Assert.Equal(
            "sample-earnings-policy-v1",
            decision.StrategyId);
    }

    private static void AssertValidationError(
        EarningsResearchPolicyLoadResult result,
        string expectedCode)
    {
        Assert.False(
            result.IsValid);
        Assert.Null(
            result.Definition);
        Assert.Null(
            result.Fingerprint);
        Assert.Contains(
            result.Validation.Errors,
            error =>
                string.Equals(
                    error.Code,
                    expectedCode,
                    StringComparison.Ordinal));
    }

    private static EarningsEvent CreateEvent(
        string eventId,
        DateTimeOffset publishedAt,
        decimal revenue,
        decimal dilutedEps,
        decimal operatingMargin)
    {
        var sourceTimestamp =
            publishedAt.AddMinutes(-1);
        var retrievedAt =
            publishedAt.AddMinutes(1);

        return new EarningsEvent(
            eventId,
            new InstrumentReference(
                "SAMPLE",
                AssetClass.Stock,
                "USD"),
            publishedAt,
            "FY2025-Q1",
            new EarningsSnapshot(
                Revenue:
                    revenue,
                DilutedEps:
                    dilutedEps,
                OperatingMargin:
                    operatingMargin),
            new ResearchSourceProvenance(
                "sample-provider",
                new Uri(
                    "https://example.invalid/research/sample"),
                sourceTimestamp,
                retrievedAt,
                "synthetic-test-fixture",
                SourceDocumentId:
                    eventId));
    }
}
