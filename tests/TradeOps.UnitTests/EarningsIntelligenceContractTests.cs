using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class EarningsIntelligenceContractTests
{
    private static readonly EarningsTargetWeightPolicy PortfolioPolicy =
        new(
            PositiveTargetWeight: 0.04m,
            NeutralTargetWeight: 0.02m,
            NegativeTargetWeight: 0m);

    [Fact]
    public void SecNormalizer_SelectsExactAccessionPeriodAndConsolidatedFacts()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026();

        var laterAccessionFact =
            new SecStructuredFact(
                "us-gaap",
                "RevenueFromContractWithCustomerExcludingAssessedTax",
                "USD",
                999_000_000_000m,
                filing.FiscalPeriodStart,
                filing.FiscalPeriodEnd,
                "0000320193-26-999999",
                "10-Q");

        var dimensionalFact =
            new SecStructuredFact(
                "us-gaap",
                "NetIncomeLoss",
                "USD",
                999_000_000_000m,
                filing.FiscalPeriodStart,
                filing.FiscalPeriodEnd,
                filing.AccessionNumber,
                filing.FormType,
                "StatementBusinessSegmentsAxis=ExampleMember");

        filing =
            filing with
            {
                Facts =
                    filing.Facts
                        .Concat(
                            [
                                laterAccessionFact,
                                dimensionalFact
                            ])
                        .ToArray()
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Equal(
            109_417_000_000m,
            facts.Revenue);
        Assert.Equal(
            29_789_000_000m,
            facts.NetIncome);
        Assert.Equal(
            2.02m,
            facts.DilutedEps);
        Assert.Equal(
            filing.AccessionNumber,
            facts.AccessionNumber);
    }

    [Fact]
    public void SecNormalizer_RejectsAmbiguousExactFact()
    {
        var filing =
            SecFixtureData.MsftQ3Fy2026();

        var duplicateRevenue =
            filing.Facts.Single(
                item =>
                    item.Concept ==
                    "RevenueFromContractWithCustomerExcludingAssessedTax");

        filing =
            filing with
            {
                Facts =
                    filing.Facts
                        .Append(
                            duplicateRevenue with
                            {
                                Value =
                                    duplicateRevenue.Value + 1m
                            })
                        .ToArray()
            };

        Assert.Throws<InvalidOperationException>(
            () =>
                SecStructuredFilingNormalizer.Normalize(
                    filing));
    }

    [Theory]
    [InlineData("AAPL", 109417000000d, 2.02d)]
    [InlineData("MSFT", 82886000000d, 4.27d)]
    [InlineData("NVDA", 96221000000d, 2.46d)]
    public void SecFixtures_NormalizeRealFilingAnchors(
        string symbol,
        double expectedRevenue,
        double expectedDilutedEps)
    {
        var filing =
            symbol switch
            {
                "AAPL" =>
                    SecFixtureData.AaplQ3Fy2026(),
                "MSFT" =>
                    SecFixtureData.MsftQ3Fy2026(),
                "NVDA" =>
                    SecFixtureData.NvdaQ2Fy2027(),
                _ =>
                    throw new ArgumentOutOfRangeException(
                        nameof(symbol))
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Equal(
            (decimal)expectedRevenue,
            facts.Revenue);
        Assert.Equal(
            (decimal)expectedDilutedEps,
            facts.DilutedEps);
        Assert.Contains(
            filing.AccessionNumber.Replace(
                "-",
                string.Empty),
            filing.SourceUri.AbsoluteUri);
    }

    [Fact]
    public void SecFactory_NormalizesStockEvent_AndComputesMargins()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026();
        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        var retrievedAt =
            filing.AcceptedAt.AddMinutes(2);

        var result =
            SecEarningsEventFactory.Create(
                facts,
                retrievedAt);

        Assert.Equal(
            filing.EventId,
            result.EventId);
        Assert.Equal(
            "AAPL",
            result.Instrument.Symbol);
        Assert.Equal(
            AssetClass.Stock,
            result.Instrument.AssetClass);
        Assert.Equal(
            "USD",
            result.Instrument.Currency);
        Assert.Equal(
            54_770_000_000m /
            109_417_000_000m,
            result.Snapshot.GrossMargin);
        Assert.Equal(
            35_695_000_000m /
            109_417_000_000m,
            result.Snapshot.OperatingMargin);
        Assert.Equal(
            TimeSpan.Zero,
            result.PublishedAt.Offset);
        Assert.Equal(
            retrievedAt.ToUniversalTime(),
            result.PublishedAt);
        Assert.Equal(
            filing.AcceptedAt.ToUniversalTime(),
            result.Provenance.SourceTimestamp);
        Assert.Equal(
            retrievedAt.ToUniversalTime(),
            result.Provenance.RetrievedAt);
        Assert.Equal(
            filing.AccessionNumber,
            result.Provenance.SourceDocumentId);
        Assert.Equal(
            SecEarningsEventFactory.ProviderName,
            result.Provenance.Provider);
    }

    [Fact]
    public void SecFactory_RejectsRetrievalBeforeSecAcceptance()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026();
        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                SecEarningsEventFactory.Create(
                    facts,
                    filing.AcceptedAt.AddSeconds(-1)));
    }

    [Fact]
    public void SecFactory_UsesVerifiedPublicAvailabilityTimestampWhenProvided()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026();

        var publiclyAvailableAt =
            filing.AcceptedAt.AddSeconds(8);
        var retrievedAt =
            filing.AcceptedAt.AddSeconds(20);

        filing =
            filing with
            {
                PubliclyAvailableAt =
                    publiclyAvailableAt
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        var result =
            SecEarningsEventFactory.Create(
                facts,
                retrievedAt);

        Assert.Equal(
            publiclyAvailableAt.ToUniversalTime(),
            result.PublishedAt);
        Assert.Equal(
            filing.AcceptedAt.ToUniversalTime(),
            result.Provenance.SourceTimestamp);
        Assert.Equal(
            retrievedAt.ToUniversalTime(),
            result.Provenance.RetrievedAt);
    }

    [Fact]
    public void SecFactory_RejectsVerifiedAvailabilityBeforeAcceptance()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026()
            with
            {
                PubliclyAvailableAt =
                    SecFixtureData.AaplQ3Fy2026()
                        .AcceptedAt
                        .AddSeconds(-1)
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                SecEarningsEventFactory.Create(
                    facts,
                    filing.AcceptedAt.AddSeconds(20)));
    }

    [Fact]
    public void SecFactory_RejectsVerifiedAvailabilityAfterRetrieval()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026();

        var retrievedAt =
            filing.AcceptedAt.AddSeconds(20);

        filing =
            filing with
            {
                PubliclyAvailableAt =
                    retrievedAt.AddSeconds(1)
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                SecEarningsEventFactory.Create(
                    facts,
                    retrievedAt));
    }

    [Fact]
    public void SecFactory_RejectsLookalikeSecHost()
    {
        var filing =
            SecFixtureData.AaplQ3Fy2026()
            with
            {
                SourceUri =
                    new Uri(
                        "https://evilsec.gov/example")
            };

        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        Assert.Throws<ArgumentException>(
            () =>
                SecEarningsEventFactory.Create(
                    facts,
                    filing.AcceptedAt.AddMinutes(1)));
    }

    [Fact]
    public void Rule_PositiveComparableKpis_ProducesTargetWeightDecisionAcceptedByPlanner()
    {
        var prior =
            SecFixtureData.Event(
                "MSFT",
                "msft-prior",
                new DateTimeOffset(
                    2026,
                    1,
                    28,
                    21,
                    7,
                    34,
                    TimeSpan.Zero),
                revenue: 70_066_000_000m,
                dilutedEps: 3.46m,
                operatingMargin:
                    32_000_000_000m /
                    70_066_000_000m);

        var current =
            SecFixtureData.CreateEvent(
                SecFixtureData.MsftQ3Fy2026());

        var decision =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                current.PublishedAt.AddSeconds(1),
                PortfolioPolicy);

        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            decision.Action);
        Assert.Equal(
            PortfolioPolicy.PositiveTargetWeight,
            decision.TargetWeight);
        Assert.Equal(
            EarningsAssessment.Positive.ToString(),
            decision.Metadata!["assessment"]);
        Assert.Equal(
            1m,
            decision.Confidence);
        Assert.Equal(
            current.EventId,
            decision.SourceEventId);
        Assert.Equal(
            current.Provenance.SourceDocumentId,
            decision.Metadata!["sourceDocumentId"]);
        Assert.True(
            ResearchDecisionValidator.Validate(
                decision).IsValid);

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                new PortfolioSnapshot(
                    "USD",
                    NetAssetValue: 100_000m,
                    Cash: 100_000m,
                    Positions:
                        Array.Empty<PortfolioPosition>(),
                    AsOf:
                        decision.GeneratedAt.AddMinutes(1)),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MaxTargetWeight: 0.10m));

        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.Equal(
            PortfolioPolicy.PositiveTargetWeight,
            plan.TargetPosition!.TargetWeight);
        Assert.NotNull(
            plan.OrderIntent);
    }

    [Fact]
    public void Rule_NegativeComparableKpis_MapsToConfiguredLongOnlyTarget()
    {
        var prior =
            SecFixtureData.Event(
                "NVDA",
                "nvda-prior",
                new DateTimeOffset(
                    2026,
                    5,
                    20,
                    20,
                    0,
                    0,
                    TimeSpan.Zero),
                revenue: 110_000_000_000m,
                dilutedEps: 3.00m,
                operatingMargin: 0.70m);

        var current =
            SecFixtureData.CreateEvent(
                SecFixtureData.NvdaQ2Fy2027());

        var decision =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                current.PublishedAt,
                PortfolioPolicy);

        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            decision.Action);
        Assert.Equal(
            PortfolioPolicy.NegativeTargetWeight,
            decision.TargetWeight);
        Assert.Equal(
            EarningsAssessment.Negative.ToString(),
            decision.Metadata!["assessment"]);
        Assert.Equal(
            "-3",
            decision.Metadata["score"]);
    }

    [Fact]
    public void Rule_NeutralAssessment_MapsToConfiguredNeutralTarget()
    {
        var prior =
            SecFixtureData.Event(
                "AAPL",
                "aapl-neutral-prior",
                new DateTimeOffset(
                    2026,
                    5,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                100m,
                2m,
                0.30m);

        var current =
            SecFixtureData.Event(
                "AAPL",
                "aapl-neutral-current",
                new DateTimeOffset(
                    2026,
                    8,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                104m,
                2.20m,
                0.30m);

        var decision =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                current.PublishedAt,
                PortfolioPolicy);

        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            decision.Action);
        Assert.Equal(
            PortfolioPolicy.NeutralTargetWeight,
            decision.TargetWeight);
        Assert.Equal(
            EarningsAssessment.Neutral.ToString(),
            decision.Metadata!["assessment"]);
        Assert.Equal(
            "1",
            decision.Metadata["score"]);
    }

    [Fact]
    public void Rule_RejectsTargetWeightPolicyOutsideLongOnlyRange()
    {
        var prior =
            SecFixtureData.Event(
                "AAPL",
                "aapl-policy-prior",
                new DateTimeOffset(
                    2026,
                    5,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                100m,
                2m,
                0.30m);

        var current =
            SecFixtureData.Event(
                "AAPL",
                "aapl-policy-current",
                new DateTimeOffset(
                    2026,
                    8,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                110m,
                2.20m,
                0.31m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                DeterministicEarningsDecisionRule.Evaluate(
                    current,
                    prior,
                    current.PublishedAt,
                    new EarningsTargetWeightPolicy(
                        PositiveTargetWeight: 1.01m,
                        NeutralTargetWeight: 0.02m,
                        NegativeTargetWeight: 0m)));
    }

    [Fact]
    public void Rule_RefusesLookAheadDecisionTimestamp()
    {
        var prior =
            SecFixtureData.Event(
                "AAPL",
                "aapl-prior",
                new DateTimeOffset(
                    2026,
                    5,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                100_000_000_000m,
                1.80m,
                0.30m);

        var current =
            SecFixtureData.CreateEvent(
                SecFixtureData.AaplQ3Fy2026());

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                DeterministicEarningsDecisionRule.Evaluate(
                    current,
                    prior,
                    current.PublishedAt.AddTicks(-1),
                    PortfolioPolicy));
    }

    [Fact]
    public void Rule_RefusesPublishedAtThatPredatesProvenanceTimestamp()
    {
        var current =
            SecFixtureData.CreateEvent(
                SecFixtureData.AaplQ3Fy2026());

        current =
            current with
            {
                PublishedAt =
                    current.PublishedAt.AddMinutes(-5)
            };

        var prior =
            SecFixtureData.Event(
                "AAPL",
                "aapl-prior",
                current.PublishedAt.AddDays(-90),
                100_000_000_000m,
                1.80m,
                0.30m);

        Assert.Throws<ArgumentException>(
            () =>
                DeterministicEarningsDecisionRule.Evaluate(
                    current,
                    prior,
                    current.Provenance.SourceTimestamp,
                    PortfolioPolicy));
    }

    [Fact]
    public void Rule_IsDeterministic_ForSameEventAndInputs()
    {
        var prior =
            SecFixtureData.Event(
                "AAPL",
                "aapl-prior",
                new DateTimeOffset(
                    2026,
                    5,
                    1,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                100_000_000_000m,
                1.80m,
                0.30m);

        var current =
            SecFixtureData.CreateEvent(
                SecFixtureData.AaplQ3Fy2026());
        var generatedAt =
            current.PublishedAt.AddSeconds(1);

        var first =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                generatedAt,
                PortfolioPolicy);

        var second =
            DeterministicEarningsDecisionRule.Evaluate(
                current,
                prior,
                generatedAt,
                PortfolioPolicy);

        Assert.Equal(
            first.DecisionId,
            second.DecisionId);
        Assert.Equal(
            first.Action,
            second.Action);
        Assert.Equal(
            first.Confidence,
            second.Confidence);
        Assert.Equal(
            first.SourceEventId,
            second.SourceEventId);
        Assert.Equal(
            first.Metadata!["score"],
            second.Metadata!["score"]);
    }
}

internal static class SecFixtureData
{
    public static SecStructuredFiling AaplQ3Fy2026() =>
        CreateFiling(
            eventId:
                "sec:AAPL:0000320193-26-000020",
            symbol:
                "AAPL",
            fiscalPeriod:
                "FY2026-Q3",
            cik:
                "0000320193",
            accession:
                "0000320193-26-000020",
            sourceUri:
                "https://www.sec.gov/Archives/edgar/data/320193/000032019326000020/aapl-20260627.htm",
            acceptedAt:
                new DateTimeOffset(
                    2026,
                    7,
                    31,
                    6,
                    1,
                    2,
                    TimeSpan.FromHours(-4)),
            periodStart:
                new DateOnly(
                    2026,
                    3,
                    29),
            periodEnd:
                new DateOnly(
                    2026,
                    6,
                    27),
            revenue:
                109_417_000_000m,
            dilutedEps:
                2.02m,
            netIncome:
                29_789_000_000m,
            grossProfit:
                54_770_000_000m,
            operatingIncome:
                35_695_000_000m);

    public static SecStructuredFiling MsftQ3Fy2026() =>
        CreateFiling(
            eventId:
                "sec:MSFT:0001193125-26-191507",
            symbol:
                "MSFT",
            fiscalPeriod:
                "FY2026-Q3",
            cik:
                "0000789019",
            accession:
                "0001193125-26-191507",
            sourceUri:
                "https://www.sec.gov/Archives/edgar/data/789019/000119312526191507/msft-20260331.htm",
            acceptedAt:
                new DateTimeOffset(
                    2026,
                    4,
                    29,
                    16,
                    6,
                    24,
                    TimeSpan.FromHours(-4)),
            periodStart:
                new DateOnly(
                    2026,
                    1,
                    1),
            periodEnd:
                new DateOnly(
                    2026,
                    3,
                    31),
            revenue:
                82_886_000_000m,
            dilutedEps:
                4.27m,
            netIncome:
                31_778_000_000m,
            grossProfit:
                56_058_000_000m,
            operatingIncome:
                38_398_000_000m);

    public static SecStructuredFiling NvdaQ2Fy2027() =>
        CreateFiling(
            eventId:
                "sec:NVDA:0001045810-26-000075",
            symbol:
                "NVDA",
            fiscalPeriod:
                "FY2027-Q2",
            cik:
                "0001045810",
            accession:
                "0001045810-26-000075",
            sourceUri:
                "https://www.sec.gov/Archives/edgar/data/1045810/000104581026000075/nvda-20260726.htm",
            acceptedAt:
                new DateTimeOffset(
                    2026,
                    8,
                    26,
                    16,
                    36,
                    0,
                    TimeSpan.FromHours(-4)),
            periodStart:
                new DateOnly(
                    2026,
                    4,
                    27),
            periodEnd:
                new DateOnly(
                    2026,
                    7,
                    26),
            revenue:
                96_221_000_000m,
            dilutedEps:
                2.46m,
            netIncome:
                59_688_000_000m,
            grossProfit:
                72_142_000_000m,
            operatingIncome:
                63_734_000_000m);

    public static EarningsEvent CreateEvent(
        SecStructuredFiling filing)
    {
        var facts =
            SecStructuredFilingNormalizer.Normalize(
                filing);

        return SecEarningsEventFactory.Create(
            facts,
            filing.AcceptedAt.AddMinutes(1));
    }

    public static EarningsEvent Event(
        string symbol,
        string eventId,
        DateTimeOffset publishedAt,
        decimal revenue,
        decimal dilutedEps,
        decimal operatingMargin) =>
        new(
            eventId,
            new InstrumentReference(
                symbol,
                AssetClass.Stock,
                "USD"),
            publishedAt,
            "fixture-period",
            new EarningsSnapshot(
                Revenue: revenue,
                DilutedEps: dilutedEps,
                NetIncome: null,
                GrossMargin: null,
                OperatingMargin:
                    operatingMargin),
            new ResearchSourceProvenance(
                "SEC",
                new Uri(
                    "https://www.sec.gov/Archives/edgar/data/fixture/fixture.htm"),
                publishedAt,
                publishedAt.AddMinutes(1),
                "fixture-v1",
                SourceDocumentId: eventId));

    private static SecStructuredFiling CreateFiling(
        string eventId,
        string symbol,
        string fiscalPeriod,
        string cik,
        string accession,
        string sourceUri,
        DateTimeOffset acceptedAt,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal revenue,
        decimal dilutedEps,
        decimal netIncome,
        decimal grossProfit,
        decimal operatingIncome)
    {
        var facts =
            new[]
            {
                Fact(
                    "RevenueFromContractWithCustomerExcludingAssessedTax",
                    "USD",
                    revenue),
                Fact(
                    "EarningsPerShareDiluted",
                    "USD/shares",
                    dilutedEps),
                Fact(
                    "NetIncomeLoss",
                    "USD",
                    netIncome),
                Fact(
                    "GrossProfit",
                    "USD",
                    grossProfit),
                Fact(
                    "OperatingIncomeLoss",
                    "USD",
                    operatingIncome)
            };

        return new SecStructuredFiling(
            eventId,
            symbol,
            fiscalPeriod,
            cik,
            accession,
            "10-Q",
            new Uri(
                sourceUri),
            acceptedAt,
            periodStart,
            periodEnd,
            "USD",
            facts);

        SecStructuredFact Fact(
            string concept,
            string unit,
            decimal value) =>
            new(
                "us-gaap",
                concept,
                unit,
                value,
                periodStart,
                periodEnd,
                accession,
                "10-Q");
    }
}
