using System.Globalization;
using System.Text.Json.Nodes;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DocFlowStructuredEarningsFactsAdapterTests
{
    [Fact]
    public void Adapt_ValidExtraction_ProducesAuditableFactSet()
    {
        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                CreateValidJson(),
                CreateInput());

        Assert.Equal(
            1,
            factSet.SchemaVersion);
        Assert.Equal(
            "deterministic-sample-engine",
            factSet.ExtractionEngine);
        Assert.Equal(
            "textdoc:sample-normalized-document",
            factSet.DocFlowDocumentId);
        Assert.Equal(
            new string('a', 64),
            factSet.DocFlowFingerprint);
        Assert.Equal(
            "USD",
            factSet.Currency);
        Assert.Equal(
            0.91m,
            factSet.ExtractionConfidence);

        Assert.Equal(
            14300000000m,
            factSet.Revenue!.Value);
        Assert.Equal(
            2.35m,
            factSet.DilutedEps!.Value);
        Assert.Equal(
            2200000000m,
            factSet.NetIncome!.Value);
        Assert.Equal(
            0.42m,
            factSet.GrossMargin!.Value);
        Assert.Equal(
            0.18m,
            factSet.OperatingMargin!.Value);

        Assert.Equal(
            new[] { "textseg:sample-1" },
            factSet.Revenue.EvidenceSegmentIds);
        Assert.Equal(
            new[] { "textseg:sample-2" },
            factSet.NetIncome.EvidenceSegmentIds);
        Assert.Equal(
            new[] { "textseg:sample-3" },
            factSet.OperatingMargin.EvidenceSegmentIds);

        Assert.NotNull(
            factSet.Guidance);
        Assert.Equal(
            EarningsGuidanceDirection.Raised,
            factSet.Guidance!.Direction!.Direction);
        Assert.Equal(
            new[] { "textseg:sample-3" },
            factSet.Guidance.Direction.EvidenceSegmentIds);
        Assert.Equal(
            15000000000m,
            factSet.Guidance.RevenueLow!.Value);
        Assert.Equal(
            15500000000m,
            factSet.Guidance.RevenueHigh!.Value);
        Assert.Equal(
            2.50m,
            factSet.Guidance.DilutedEpsLow!.Value);
        Assert.Equal(
            2.70m,
            factSet.Guidance.DilutedEpsHigh!.Value);
    }

    [Fact]
    public void ToSnapshot_MapsValidatedFactsDirectly()
    {
        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                CreateValidJson(),
                CreateInput());

        var snapshot =
            EarningsTranscriptFactSetMapper.ToSnapshot(
                factSet);

        Assert.Equal(
            14300000000m,
            snapshot.Revenue);
        Assert.Equal(
            2.35m,
            snapshot.DilutedEps);
        Assert.Equal(
            2200000000m,
            snapshot.NetIncome);
        Assert.Equal(
            0.42m,
            snapshot.GrossMargin);
        Assert.Equal(
            0.18m,
            snapshot.OperatingMargin);

        Assert.NotNull(
            snapshot.Guidance);
        Assert.Equal(
            EarningsGuidanceDirection.Raised,
            snapshot.Guidance!.Direction);
        Assert.Equal(
            15000000000m,
            snapshot.Guidance.RevenueLow);
        Assert.Equal(
            15500000000m,
            snapshot.Guidance.RevenueHigh);
        Assert.Equal(
            2.50m,
            snapshot.Guidance.DilutedEpsLow);
        Assert.Equal(
            2.70m,
            snapshot.Guidance.DilutedEpsHigh);
    }

    [Fact]
    public void CreateEvent_ReusesInputIdentityAndPreservesProvenance()
    {
        var instrument =
            new InstrumentReference(
                "SAMP",
                AssetClass.Stock,
                "USD",
                "sample-venue-id",
                "SAMPLE");

        var input =
            CreateInput(
                instrument);

        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                CreateValidJson(),
                input);

        var earningsEvent =
            EarningsTranscriptEventFactory.Create(
                input,
                factSet);

        Assert.Equal(
            input.EarningsEventAssociationId,
            earningsEvent.EventId);
        Assert.Same(
            instrument,
            earningsEvent.Instrument);
        Assert.Equal(
            input.FiscalPeriod,
            earningsEvent.FiscalPeriod);
        Assert.Equal(
            input.PublishedAt,
            earningsEvent.PublishedAt);

        Assert.Equal(
            input.Provenance.Provider,
            earningsEvent.Provenance.Provider);
        Assert.Same(
            input.Provenance.SourceUri,
            earningsEvent.Provenance.SourceUri);
        Assert.Equal(
            input.Provenance.SourceTimestamp,
            earningsEvent.Provenance.SourceTimestamp);
        Assert.Equal(
            input.Provenance.RetrievedAt,
            earningsEvent.Provenance.RetrievedAt);
        Assert.Equal(
            input.Provenance.SourceDocumentId,
            earningsEvent.Provenance.SourceDocumentId);
        Assert.Equal(
            input.Provenance.IssuerId,
            earningsEvent.Provenance.IssuerId);
        Assert.Equal(
            EarningsTranscriptEventFactory.ExtractionMethodName,
            earningsEvent.Provenance.ExtractionMethod);
        Assert.NotEqual(
            factSet.ExtractionEngine,
            earningsEvent.Provenance.ExtractionMethod);
    }

    [Fact]
    public void Pipeline_SameInputs_ProducesSameSemanticEvent()
    {
        var input =
            CreateInput();

        var first =
            EarningsTranscriptEventFactory.Create(
                input,
                DocFlowStructuredEarningsFactsAdapter.Adapt(
                    CreateValidJson(),
                    input));

        var second =
            EarningsTranscriptEventFactory.Create(
                input,
                DocFlowStructuredEarningsFactsAdapter.Adapt(
                    CreateValidJson(),
                    input));

        Assert.Equal(
            first,
            second);
    }

    [Fact]
    public void Adapt_DocumentIdMismatch_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["docflow_document_id"] =
            "textdoc:other-document";

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_FingerprintMismatch_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["docflow_fingerprint"] =
            new string(
                'b',
                64);

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_InvalidFingerprint_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["docflow_fingerprint"] =
            new string(
                'A',
                64);

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_UnsupportedSchemaVersion_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["schema_version"] =
            2;

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_CurrencyMismatch_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["currency"] =
            "EUR";

        AssertInvalid(
            root);
    }

    [Theory]
    [InlineData("amount_scale", "millions")]
    [InlineData("margin_scale", "percent")]
    public void Adapt_InvalidScale_Rejects(
        string fieldName,
        string value)
    {
        var root =
            ParseValidRoot();

        root["data"]![fieldName] =
            value;

        AssertInvalid(
            root);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("incomplete")]
    public void Adapt_FailedGenericValidationStatus_Rejects(
        string status)
    {
        var root =
            ParseValidRoot();

        root["validation_status"] =
            status;

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_MissingFactEvidence_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["facts"]!["revenue"]!
            .AsObject()
            .Remove(
                "evidence_segment_ids");

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_UnknownEvidenceSegment_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["facts"]!["revenue"]!["evidence_segment_ids"] =
            JsonNode.Parse(
                """["textseg:unknown"]""");

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_DuplicateEvidenceWithinFact_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["facts"]!["revenue"]!["evidence_segment_ids"] =
            JsonNode.Parse(
                """["textseg:sample-1","textseg:sample-1"]""");

        AssertInvalid(
            root);
    }

    [Theory]
    [InlineData("unexpected_root")]
    [InlineData("unexpected_data")]
    [InlineData("unexpected_facts")]
    [InlineData("unexpected_guidance")]
    [InlineData("unexpected_fact")]
    public void Adapt_UnknownJsonMember_Rejects(
        string location)
    {
        var root =
            ParseValidRoot();

        switch (location)
        {
            case "unexpected_root":
                root["unexpected"] =
                    true;
                break;
            case "unexpected_data":
                root["data"]!["unexpected"] =
                    true;
                break;
            case "unexpected_facts":
                root["data"]!["facts"]!["unexpected"] =
                    true;
                break;
            case "unexpected_guidance":
                root["data"]!["guidance"]!["unexpected"] =
                    true;
                break;
            case "unexpected_fact":
                root["data"]!["facts"]!["revenue"]!["unexpected"] =
                    true;
                break;
            default:
                throw new InvalidOperationException();
        }

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_UnsupportedGuidanceDirection_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["guidance"]!["direction"]!["value"] =
            "unknown";

        AssertInvalid(
            root);
    }

    [Theory]
    [InlineData("revenue_low", "revenue_high", "16000000000", "15500000000")]
    [InlineData("diluted_eps_low", "diluted_eps_high", "3.0", "2.7")]
    public void Adapt_InvalidGuidanceRange_Rejects(
        string lowName,
        string highName,
        string lowText,
        string highText)
    {
        var root =
            ParseValidRoot();

        root["data"]!["guidance"]![lowName]!["value"] =
            decimal.Parse(
                lowText,
                CultureInfo.InvariantCulture);
        root["data"]!["guidance"]![highName]!["value"] =
            decimal.Parse(
                highText,
                CultureInfo.InvariantCulture);

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_NoUsefulFacts_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!["facts"] =
            new JsonObject();
        root["data"]!["guidance"] =
            new JsonObject();

        AssertInvalid(
            root);
    }

    [Fact]
    public void Snapshot_EmptyGuidanceObject_MapsToNullGuidance()
    {
        var root =
            ParseValidRoot();

        root["data"]!["guidance"] =
            new JsonObject();

        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                root.ToJsonString(),
                CreateInput());

        var snapshot =
            EarningsTranscriptFactSetMapper.ToSnapshot(
                factSet);

        Assert.Null(
            factSet.Guidance);
        Assert.Null(
            snapshot.Guidance);
    }

    [Fact]
    public void Adapt_GuidanceOnly_IsUsefulExtraction()
    {
        var root =
            ParseValidRoot();

        root["data"]!["facts"] =
            new JsonObject();

        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                root.ToJsonString(),
                CreateInput());

        Assert.Null(
            factSet.Revenue);
        Assert.NotNull(
            factSet.Guidance);
    }

    [Fact]
    public void Adapt_MissingGuidanceContainer_Rejects()
    {
        var root =
            ParseValidRoot();

        root["data"]!
            .AsObject()
            .Remove(
                "guidance");

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_NonTranscriptDocumentType_Rejects()
    {
        var root =
            ParseValidRoot();

        root["document_type"] =
            "filing";

        AssertInvalid(
            root);
    }

    [Fact]
    public void Adapt_ConfidenceOutsideDocFlowRange_Rejects()
    {
        var root =
            ParseValidRoot();

        root["confidence"] =
            1.01m;

        AssertInvalid(
            root);
    }

    [Fact]
    public void CreateEvent_InvalidAvailabilityInvariant_Rejects()
    {
        var input =
            CreateInput() with
            {
                PublishedAt =
                    DateTimeOffset.Parse(
                        "2026-09-30T13:00:00Z")
            };

        var factSet =
            DocFlowStructuredEarningsFactsAdapter.Adapt(
                CreateValidJson(),
                input);

        Assert.Throws<ArgumentException>(
            () =>
                EarningsTranscriptEventFactory.Create(
                    input,
                    factSet));
    }

    [Fact]
    public void Boundary_DoesNotExposeResearchDecision()
    {
        var forbidden =
            typeof(ResearchDecision);

        Assert.DoesNotContain(
            forbidden,
            typeof(EarningsTranscriptFactSet)
                .GetProperties()
                .Select(property => property.PropertyType));

        Assert.DoesNotContain(
            forbidden,
            typeof(EarningsEvent)
                .GetProperties()
                .Select(property => property.PropertyType));

        Assert.DoesNotContain(
            forbidden,
            typeof(EarningsSnapshot)
                .GetProperties()
                .Select(property => property.PropertyType));
    }

    private static void AssertInvalid(
        JsonObject root)
    {
        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowStructuredEarningsFactsAdapter.Adapt(
                    root.ToJsonString(),
                    CreateInput()));
    }

    private static EarningsTranscriptResearchInput CreateInput(
        InstrumentReference? instrument = null)
    {
        instrument ??=
            new InstrumentReference(
                "SAMP",
                AssetClass.Stock,
                "USD");

        var provenance =
            new ResearchSourceProvenance(
                "sample-provider",
                new Uri(
                    "https://example.invalid/transcripts/sample-q3-2026"),
                DateTimeOffset.Parse(
                    "2026-09-30T14:00:00Z"),
                DateTimeOffset.Parse(
                    "2026-10-01T08:30:00Z"),
                DocFlowEarningsResearchAdapter.ExtractionMethod,
                "sample-call-2026-q3",
                "issuer-sample");

        return new EarningsTranscriptResearchInput(
            instrument,
            "FY2026-Q3",
            "earnings:sample:2026-q3",
            "Sample Company Q3 2026 earnings call",
            DateTimeOffset.Parse(
                "2026-09-30T15:00:00Z"),
            provenance,
            "textdoc:sample-normalized-document",
            new string(
                'a',
                64),
            new[]
            {
                new EarningsTranscriptParticipant(
                    "speaker-a",
                    "Speaker A",
                    "management",
                    "Sample Company"),
                new EarningsTranscriptParticipant(
                    "analyst-1",
                    "Analyst",
                    "analyst",
                    "Sample Research")
            },
            new[]
            {
                new EarningsTranscriptSegment(
                    1,
                    "textseg:sample-1",
                    "speaker-a",
                    "Revenue was 14.3 billion dollars."),
                new EarningsTranscriptSegment(
                    2,
                    "textseg:sample-2",
                    "speaker-a",
                    "Gross margin was 42 percent and net income was 2.2 billion dollars."),
                new EarningsTranscriptSegment(
                    3,
                    "textseg:sample-3",
                    "speaker-a",
                    "We raised guidance for the next period.")
            });
    }

    private static JsonObject ParseValidRoot() =>
        JsonNode
            .Parse(
                CreateValidJson())!
            .AsObject();

    private static string CreateValidJson() =>
        $$"""
        {
          "engine": "deterministic-sample-engine",
          "document_type": "transcript",
          "data": {
            "schema_version": 1,
            "docflow_document_id": "textdoc:sample-normalized-document",
            "docflow_fingerprint": "{{new string('a', 64)}}",
            "currency": "USD",
            "amount_scale": "base_units",
            "margin_scale": "fraction",
            "facts": {
              "revenue": {
                "value": 14300000000,
                "evidence_segment_ids": ["textseg:sample-1"]
              },
              "diluted_eps": {
                "value": 2.35,
                "evidence_segment_ids": ["textseg:sample-1"]
              },
              "net_income": {
                "value": 2200000000,
                "evidence_segment_ids": ["textseg:sample-2"]
              },
              "gross_margin": {
                "value": 0.42,
                "evidence_segment_ids": ["textseg:sample-2"]
              },
              "operating_margin": {
                "value": 0.18,
                "evidence_segment_ids": ["textseg:sample-3"]
              }
            },
            "guidance": {
              "direction": {
                "value": "raised",
                "evidence_segment_ids": ["textseg:sample-3"]
              },
              "revenue_low": {
                "value": 15000000000,
                "evidence_segment_ids": ["textseg:sample-3"]
              },
              "revenue_high": {
                "value": 15500000000,
                "evidence_segment_ids": ["textseg:sample-3"]
              },
              "diluted_eps_low": {
                "value": 2.50,
                "evidence_segment_ids": ["textseg:sample-3"]
              },
              "diluted_eps_high": {
                "value": 2.70,
                "evidence_segment_ids": ["textseg:sample-3"]
              }
            }
          },
          "confidence": 0.91,
          "validation_status": "valid",
          "validation": {
            "status": "valid",
            "checks": {},
            "confidence": 1.0
          }
        }
        """;
}
