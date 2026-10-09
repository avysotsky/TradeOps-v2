using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ResearchDecisionValidatorTests
{
    [Fact]
    public void Validate_StockTargetWeight_NormalizesBrokerNeutralIdentity()
    {
        var generatedAt =
            new DateTimeOffset(
                2026,
                10,
                6,
                12,
                0,
                0,
                TimeSpan.FromHours(2));

        var decision =
            new ResearchDecision(
                " AAPL-Q4-2026-guidance ",
                " earnings-quality-v1 ",
                new InstrumentReference(
                    " aapl ",
                    AssetClass.Stock,
                    " usd ",
                    " 265598 ",
                    " smart "),
                ResearchDecisionAction.SetTargetWeight,
                generatedAt,
                TargetWeight: 0.04m,
                Confidence: 0.82m,
                SourceEventId: " AAPL-Q4-2026 ",
                Reason: " guidance raised ",
                ValidUntil:
                    generatedAt.AddDays(7),
                Metadata:
                    new Dictionary<string, string>
                    {
                        [" epsSurprise "] = " 0.08 "
                    });

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);

        var normalized =
            Assert.IsType<ResearchDecision>(
                result.NormalizedDecision);

        Assert.Equal(
            "AAPL-Q4-2026-guidance",
            normalized.DecisionId);
        Assert.Equal(
            "earnings-quality-v1",
            normalized.StrategyId);
        Assert.Equal(
            "AAPL",
            normalized.Instrument.Symbol);
        Assert.Equal(
            AssetClass.Stock,
            normalized.Instrument.AssetClass);
        Assert.Equal(
            "USD",
            normalized.Instrument.Currency);
        Assert.Equal(
            "265598",
            normalized.Instrument.VenueInstrumentId);
        Assert.Equal(
            "SMART",
            normalized.Instrument.Exchange);
        Assert.Equal(
            TimeSpan.Zero,
            normalized.GeneratedAt.Offset);
        Assert.Equal(
            "0.08",
            normalized.Metadata!["epsSurprise"]);
    }

    [Fact]
    public void Validate_SetTargetWeightWithoutWeight_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.SetTargetWeight)
            with
            {
                TargetWeight = null
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            nameof(ResearchDecision.TargetWeight),
            result.Errors.Keys);
        Assert.Null(result.NormalizedDecision);
    }

    [Fact]
    public void Validate_TargetWeightOnImmediateAction_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.Buy)
            with
            {
                TargetWeight = 0.10m
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            nameof(ResearchDecision.TargetWeight),
            result.Errors.Keys);
    }

    [Fact]
    public void Validate_UnknownAction_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.NoAction)
            with
            {
                Action =
                    ResearchDecisionAction.Unknown
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            nameof(ResearchDecision.Action),
            result.Errors.Keys);
    }

    [Fact]
    public void Validate_ConfidenceOutsideZeroToOne_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.Buy)
            with
            {
                Confidence = 1.01m
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            nameof(ResearchDecision.Confidence),
            result.Errors.Keys);
    }

    [Fact]
    public void Validate_UnknownAssetClass_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.NoAction)
            with
            {
                Instrument =
                    new InstrumentReference(
                        "AAPL",
                        AssetClass.Unknown,
                        "USD",
                        "265598",
                        "SMART")
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            "Instrument.AssetClass",
            result.Errors.Keys);
    }

    [Fact]
    public void Validate_ExpiredAtCreation_IsRejected()
    {
        var decision =
            CreateValid(
                ResearchDecisionAction.NoAction);

        decision =
            decision with
            {
                ValidUntil =
                    decision.GeneratedAt
            };

        var result =
            ResearchDecisionValidator.Validate(
                decision);

        Assert.False(result.IsValid);
        Assert.Contains(
            nameof(ResearchDecision.ValidUntil),
            result.Errors.Keys);
    }

    private static ResearchDecision CreateValid(
        ResearchDecisionAction action) =>
        new(
            "decision-1",
            "strategy-1",
            new InstrumentReference(
                "AAPL",
                AssetClass.Stock,
                "USD",
                "265598",
                "SMART"),
            action,
            DateTimeOffset.UtcNow,
            TargetWeight:
                action ==
                ResearchDecisionAction.SetTargetWeight
                    ? 0.05m
                    : null,
            Confidence: 0.75m,
            SourceEventId: "event-1",
            Reason: "test");
}
