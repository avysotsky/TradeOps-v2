using System.Text.Json.Nodes;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DocFlowEarningsResearchAdapterTests
{
    [Fact]
    public void Adapt_ValidTranscript_MapsDeterministicEarningsInput()
    {
        var instrument =
            new InstrumentReference(
                "SAMP",
                AssetClass.Stock,
                "USD",
                "sample-venue-id",
                "SAMPLE");

        var context =
            new EarningsTranscriptResearchContext(
                instrument,
                "FY2026-Q3",
                "earnings:sample:2026-q3",
                "issuer-sample");

        var first =
            DocFlowEarningsResearchAdapter.Adapt(
                CreateValidJson(),
                context);

        var second =
            DocFlowEarningsResearchAdapter.Adapt(
                CreateValidJson(),
                context);

        Assert.Same(
            instrument,
            first.Instrument);
        Assert.Equal(
            "FY2026-Q3",
            first.FiscalPeriod);
        Assert.Equal(
            "earnings:sample:2026-q3",
            first.EarningsEventAssociationId);
        Assert.Equal(
            "Sample Company Q3 2026 earnings call",
            first.Title);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-09-30T15:00:00Z"),
            first.PublishedAt);
        Assert.Equal(
            "textdoc:sample-normalized-document",
            first.DocFlowDocumentId);
        Assert.Equal(
            new string('a', 64),
            first.DocFlowFingerprint);

        Assert.IsType<ResearchSourceProvenance>(
            first.Provenance);
        Assert.Equal(
            "sample-provider",
            first.Provenance.Provider);
        Assert.Equal(
            "https://example.invalid/transcripts/sample-q3-2026",
            first.Provenance.SourceUri.AbsoluteUri);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-09-30T14:00:00Z"),
            first.Provenance.SourceTimestamp);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-10-01T08:30:00Z"),
            first.Provenance.RetrievedAt);
        Assert.Equal(
            DocFlowEarningsResearchAdapter.ExtractionMethod,
            first.Provenance.ExtractionMethod);
        Assert.Equal(
            "sample-call-2026-q3",
            first.Provenance.SourceDocumentId);
        Assert.Equal(
            "issuer-sample",
            first.Provenance.IssuerId);

        Assert.Equal(
            first.PublishedAt,
            second.PublishedAt);
        Assert.Equal(
            first.Provenance,
            second.Provenance);
        Assert.Equal(
            first.Participants.ToArray(),
            second.Participants.ToArray());
        Assert.Equal(
            first.Segments.ToArray(),
            second.Segments.ToArray());
    }

    [Fact]
    public void Adapt_PreservesSegmentOrderAndParticipantAssociation()
    {
        var result =
            DocFlowEarningsResearchAdapter.Adapt(
                CreateValidJson(),
                CreateContext());

        Assert.Collection(
            result.Participants,
            participant =>
            {
                Assert.Equal(
                    "speaker-a",
                    participant.ParticipantId);
                Assert.Equal(
                    "Speaker A",
                    participant.DisplayName);
                Assert.Equal(
                    "management",
                    participant.Role);
                Assert.Equal(
                    "Sample Company",
                    participant.Organization);
            },
            participant =>
            {
                Assert.Equal(
                    "analyst-1",
                    participant.ParticipantId);
                Assert.Equal(
                    "Analyst",
                    participant.DisplayName);
            });

        Assert.Collection(
            result.Segments,
            segment =>
            {
                Assert.Equal(
                    1,
                    segment.Sequence);
                Assert.Equal(
                    "textseg:sample-1",
                    segment.DocFlowSegmentId);
                Assert.Equal(
                    "speaker-a",
                    segment.ParticipantId);
            },
            segment =>
            {
                Assert.Equal(
                    2,
                    segment.Sequence);
                Assert.Equal(
                    "analyst-1",
                    segment.ParticipantId);
            },
            segment =>
            {
                Assert.Equal(
                    3,
                    segment.Sequence);
                Assert.Null(
                    segment.ParticipantId);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/transcript")]
    public void Adapt_MissingOrInvalidSourceUri_Rejects(
        string? sourceUri)
    {
        var root =
            ParseValidRoot();

        root["source"]!["source_uri"] =
            sourceUri;

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Theory]
    [InlineData("source_timestamp")]
    [InlineData("published_at")]
    [InlineData("retrieved_at")]
    public void Adapt_MissingRequiredTimestamp_Rejects(
        string propertyName)
    {
        var root =
            ParseValidRoot();

        root["source"]!
            .AsObject()
            .Remove(propertyName);

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_TimestampWithoutTimezone_Rejects()
    {
        var root =
            ParseValidRoot();

        root["source"]!["published_at"] =
            "2026-09-30T15:00:00";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_NonUtcNormalizedTimestamp_Rejects()
    {
        var root =
            ParseValidRoot();

        root["source"]!["published_at"] =
            "2026-09-30T18:00:00+03:00";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_InvalidTimestampOrdering_Rejects()
    {
        var root =
            ParseValidRoot();

        root["source"]!["published_at"] =
            "2026-09-30T13:59:59Z";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_NonTranscriptDocumentType_Rejects()
    {
        var root =
            ParseValidRoot();

        root["document_type"] =
            "filing";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Adapt_MissingFiscalPeriod_Rejects(
        string fiscalPeriod)
    {
        Assert.Throws<ArgumentException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    CreateValidJson(),
                    CreateContext(
                        fiscalPeriod: fiscalPeriod)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Adapt_MissingEarningsEventAssociation_Rejects(
        string associationId)
    {
        Assert.Throws<ArgumentException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    CreateValidJson(),
                    CreateContext(
                        associationId: associationId)));
    }

    [Fact]
    public void Adapt_UnknownRootMember_Rejects()
    {
        var root =
            ParseValidRoot();

        root["unexpected"] =
            true;

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_UnknownNestedMember_Rejects()
    {
        var root =
            ParseValidRoot();

        root["source"]!["unexpected"] =
            true;

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_InvalidFingerprint_Rejects()
    {
        var root =
            ParseValidRoot();

        root["fingerprint"] =
            new string(
                'A',
                64);

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_EmptySegments_Rejects()
    {
        var root =
            ParseValidRoot();

        root["segments"] =
            new JsonArray();

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_UnorderedOrDuplicateSegmentSequence_Rejects()
    {
        var root =
            ParseValidRoot();

        root["segments"]![1]!["sequence"] =
            1;

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_UnknownParticipantReference_Rejects()
    {
        var root =
            ParseValidRoot();

        root["segments"]![0]!["participant_id"] =
            "unknown-speaker";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void Adapt_DuplicateParticipantId_Rejects()
    {
        var root =
            ParseValidRoot();

        root["participants"]![1]!["participant_id"] =
            "speaker-a";

        Assert.Throws<InvalidDataException>(
            () =>
                DocFlowEarningsResearchAdapter.Adapt(
                    root.ToJsonString(),
                    CreateContext()));
    }

    [Fact]
    public void AdapterOutput_DoesNotExposeForbiddenDownstreamContracts()
    {
        var outputPropertyTypes =
            typeof(EarningsTranscriptResearchInput)
                .GetProperties()
                .Select(property => property.PropertyType)
                .ToArray();

        Assert.DoesNotContain(
            typeof(ResearchDecision),
            outputPropertyTypes);
        Assert.DoesNotContain(
            typeof(EarningsEvent),
            outputPropertyTypes);
        Assert.DoesNotContain(
            typeof(EarningsSnapshot),
            outputPropertyTypes);
    }

    private static EarningsTranscriptResearchContext
        CreateContext(
            string fiscalPeriod = "FY2026-Q3",
            string associationId = "earnings:sample:2026-q3")
    {
        return new EarningsTranscriptResearchContext(
            new InstrumentReference(
                "SAMP",
                AssetClass.Stock,
                "USD"),
            fiscalPeriod,
            associationId,
            "issuer-sample");
    }

    private static JsonObject ParseValidRoot() =>
        JsonNode
            .Parse(
                CreateValidJson())!
            .AsObject();

    private static string CreateValidJson() =>
        $$"""
        {
          "document_id": "textdoc:sample-normalized-document",
          "title": "Sample Company Q3 2026 earnings call",
          "document_type": "transcript",
          "source": {
            "provider": "sample-provider",
            "source_uri": "https://example.invalid/transcripts/sample-q3-2026",
            "source_document_id": "sample-call-2026-q3",
            "source_timestamp": "2026-09-30T14:00:00Z",
            "published_at": "2026-09-30T15:00:00Z",
            "retrieved_at": "2026-10-01T08:30:00Z"
          },
          "participants": [
            {
              "participant_id": "speaker-a",
              "display_name": "Speaker A",
              "role": "management",
              "organization": "Sample Company"
            },
            {
              "participant_id": "analyst-1",
              "display_name": "Analyst",
              "role": "analyst",
              "organization": "Sample Research"
            }
          ],
          "segments": [
            {
              "sequence": 1,
              "participant_id": "speaker-a",
              "text": "We completed the planned service rollout.",
              "segment_id": "textseg:sample-1"
            },
            {
              "sequence": 2,
              "participant_id": "analyst-1",
              "text": "How did customers respond?",
              "segment_id": "textseg:sample-2"
            },
            {
              "sequence": 3,
              "participant_id": null,
              "text": "The operator opens the next question.",
              "segment_id": "textseg:sample-3"
            }
          ],
          "fingerprint": "{{new string('a', 64)}}"
        }
        """;
}
