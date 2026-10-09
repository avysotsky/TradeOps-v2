using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Services;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ProviderCompatibleEarningsExtractionSchemaTests
{
    private static readonly string[] FactNames =
    [
        "revenue",
        "diluted_eps",
        "net_income",
        "gross_margin",
        "operating_margin"
    ];

    private static readonly string[] GuidanceNames =
    [
        "direction",
        "revenue_low",
        "revenue_high",
        "diluted_eps_low",
        "diluted_eps_high"
    ];

    [Fact]
    public void Schema_request_shape_and_constants_match_existing_adapter()
    {
        var request =
            LoadJson(
                GetSchemaRequestPath());

        AssertExactPropertyNames(
            request,
            "json_schema",
            "schema_name",
            "schema_version");

        Assert.Equal(
            "tradeops_earnings_transcript_facts_v1",
            request.GetProperty("schema_name")
                .GetString());
        Assert.Equal(
            1,
            request.GetProperty("schema_version")
                .GetInt32());

        var schema =
            request.GetProperty("json_schema");

        Assert.True(
            TypeIncludes(
                schema,
                "object"));

        AssertExactPropertyNames(
            schema.GetProperty("properties"),
            "amount_scale",
            "currency",
            "docflow_document_id",
            "docflow_fingerprint",
            "facts",
            "guidance",
            "margin_scale",
            "schema_version");

        var properties =
            schema.GetProperty("properties");

        Assert.Equal(
            DocFlowStructuredEarningsFactsAdapter
                .SupportedSchemaVersion,
            properties.GetProperty("schema_version")
                .GetProperty("const")
                .GetInt32());
        Assert.Equal(
            DocFlowStructuredEarningsFactsAdapter
                .AmountScale,
            properties.GetProperty("amount_scale")
                .GetProperty("const")
                .GetString());
        Assert.Equal(
            DocFlowStructuredEarningsFactsAdapter
                .MarginScale,
            properties.GetProperty("margin_scale")
                .GetProperty("const")
                .GetString());
    }

    [Fact]
    public void Schema_every_object_is_closed_and_requires_every_declared_property()
    {
        var request =
            LoadJson(
                GetSchemaRequestPath());

        AssertObjectSchemasClosedAndRequired(
            request.GetProperty("json_schema"));
    }

    [Fact]
    public void Schema_fact_and_guidance_slots_are_nullable_and_match_vs07_names()
    {
        var request =
            LoadJson(
                GetSchemaRequestPath());
        var rootProperties =
            request.GetProperty("json_schema")
                .GetProperty("properties");
        var facts =
            rootProperties.GetProperty("facts");
        var guidance =
            rootProperties.GetProperty("guidance");

        AssertExactPropertyNames(
            facts.GetProperty("properties"),
            FactNames);
        AssertExactPropertyNames(
            guidance.GetProperty("properties"),
            GuidanceNames);

        foreach (var factName in FactNames)
        {
            AssertNullableEvidenceBearingObject(
                facts.GetProperty("properties")
                    .GetProperty(factName));
        }

        foreach (var guidanceName in GuidanceNames)
        {
            AssertNullableEvidenceBearingObject(
                guidance.GetProperty("properties")
                    .GetProperty(guidanceName));
        }

        var directionValue =
            guidance.GetProperty("properties")
                .GetProperty("direction")
                .GetProperty("properties")
                .GetProperty("value");

        Assert.Equal(
            new[]
            {
                "lowered",
                "maintained",
                "raised"
            },
            directionValue.GetProperty("enum")
                .EnumerateArray()
                .Select(
                    item =>
                        item.GetString())
                .ToArray());
    }

    [Fact]
    public void Schema_request_contains_no_provider_model_or_api_key_configuration_fields()
    {
        var request =
            LoadJson(
                GetSchemaRequestPath());
        var blockedNames =
            new HashSet<string>(
                new[]
                {
                    "provider",
                    "model",
                    "api_key",
                    "apiKey",
                    "openai_api_key"
                },
                StringComparer.Ordinal);

        foreach (var propertyName in EnumerateJsonPropertyNames(request))
        {
            Assert.DoesNotContain(
                propertyName,
                blockedNames);
        }
    }

    [Theory]
    [InlineData(
        "prior-raw.json",
        "100,000,000 USD",
        "2.00 USD per diluted share",
        "20%")]
    [InlineData(
        "current-raw.json",
        "110,000,000 USD",
        "2.20 USD per diluted share",
        "22%")]
    public void Provider_demo_raw_inputs_match_df02_shape_and_financial_units(
        string fileName,
        string revenueText,
        string epsText,
        string marginText)
    {
        var raw =
            LoadJson(
                GetProviderDemoPath(
                    fileName));

        AssertExactPropertyNames(
            raw,
            "document_type",
            "participants",
            "segments",
            "source",
            "title");
        Assert.Equal(
            "transcript",
            raw.GetProperty("document_type")
                .GetString());

        var source =
            raw.GetProperty("source");

        AssertExactPropertyNames(
            source,
            "provider",
            "published_at",
            "retrieved_at",
            "source_document_id",
            "source_timestamp",
            "source_uri");

        var sourceUri =
            source.GetProperty("source_uri")
                .GetString();

        Assert.True(
            Uri.TryCreate(
                sourceUri,
                UriKind.Absolute,
                out var parsedUri));
        Assert.Equal(
            "example.test",
            parsedUri!.Host);

        var sourceTimestamp =
            ParseTimestamp(
                source,
                "source_timestamp");
        var publishedAt =
            ParseTimestamp(
                source,
                "published_at");
        var retrievedAt =
            ParseTimestamp(
                source,
                "retrieved_at");

        Assert.True(
            sourceTimestamp <= publishedAt);
        Assert.True(
            publishedAt <= retrievedAt);

        foreach (var participant in
                 raw.GetProperty("participants")
                     .EnumerateArray())
        {
            AssertExactPropertyNames(
                participant,
                "display_name",
                "organization",
                "participant_id",
                "role");
            Assert.Equal(
                "Synthetic Example Company",
                participant.GetProperty("organization")
                    .GetString());
        }

        foreach (var segment in
                 raw.GetProperty("segments")
                     .EnumerateArray())
        {
            AssertExactPropertyNames(
                segment,
                "participant_id",
                "sequence",
                "text");
        }

        foreach (var propertyName in EnumerateJsonPropertyNames(raw))
        {
            Assert.DoesNotContain(
                propertyName,
                new[]
                {
                    "document_id",
                    "docflow_document_id",
                    "docflow_fingerprint",
                    "evidence_segment_ids",
                    "fingerprint",
                    "segment_id"
                });
        }

        var transcriptText =
            string.Join(
                "\n",
                raw.GetProperty("segments")
                    .EnumerateArray()
                    .Select(
                        segment =>
                            segment.GetProperty("text")
                                .GetString()));

        Assert.Contains(
            "figures in this transcript are stated in USD",
            transcriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            revenueText,
            transcriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            epsText,
            transcriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            marginText,
            transcriptText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Current_provider_demo_contains_explicit_raised_guidance()
    {
        var raw =
            LoadJson(
                GetProviderDemoPath(
                    "current-raw.json"));
        var transcriptText =
            string.Join(
                "\n",
                raw.GetProperty("segments")
                    .EnumerateArray()
                    .Select(
                        segment =>
                            segment.GetProperty("text")
                                .GetString()));

        Assert.Contains(
            "raised our next-quarter guidance",
            transcriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            "115,000,000 USD to 120,000,000 USD",
            transcriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            "2.25 USD to 2.35 USD per diluted share",
            transcriptText,
            StringComparison.Ordinal);
    }

    private static void AssertNullableEvidenceBearingObject(
        JsonElement schema)
    {
        Assert.True(
            TypeIncludes(
                schema,
                "object"));
        Assert.True(
            TypeIncludes(
                schema,
                "null"));
        AssertExactPropertyNames(
            schema.GetProperty("properties"),
            "evidence_segment_ids",
            "value");
    }

    private static void AssertObjectSchemasClosedAndRequired(
        JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TypeIncludes(
                    element,
                    "object"))
            {
                Assert.True(
                    element.TryGetProperty(
                        "additionalProperties",
                        out var additionalProperties));
                Assert.Equal(
                    JsonValueKind.False,
                    additionalProperties.ValueKind);

                Assert.True(
                    element.TryGetProperty(
                        "properties",
                        out var properties));
                Assert.True(
                    element.TryGetProperty(
                        "required",
                        out var required));

                var declared =
                    properties.EnumerateObject()
                        .Select(
                            property =>
                                property.Name)
                        .OrderBy(
                            name =>
                                name,
                            StringComparer.Ordinal)
                        .ToArray();
                var requiredNames =
                    required.EnumerateArray()
                        .Select(
                            item =>
                                item.GetString()!)
                        .OrderBy(
                            name =>
                                name,
                            StringComparer.Ordinal)
                        .ToArray();

                Assert.Equal(
                    declared,
                    requiredNames);
            }

            foreach (var property in element.EnumerateObject())
            {
                AssertObjectSchemasClosedAndRequired(
                    property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertObjectSchemasClosedAndRequired(
                    item);
            }
        }
    }

    private static bool TypeIncludes(
        JsonElement schema,
        string expected)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty(
                "type",
                out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(
                type.GetString(),
                expected,
                StringComparison.Ordinal);
        }

        return type.ValueKind == JsonValueKind.Array
            && type.EnumerateArray()
                .Any(
                    item =>
                        string.Equals(
                            item.GetString(),
                            expected,
                            StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateJsonPropertyNames(
        JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;

                foreach (var descendant in
                         EnumerateJsonPropertyNames(
                             property.Value))
                {
                    yield return descendant;
                }
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in
                         EnumerateJsonPropertyNames(
                             item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static DateTimeOffset ParseTimestamp(
        JsonElement source,
        string propertyName)
    {
        var value =
            source.GetProperty(propertyName)
                .GetString();

        Assert.False(
            string.IsNullOrWhiteSpace(
                value));

        return DateTimeOffset.Parse(
            value!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static void AssertExactPropertyNames(
        JsonElement element,
        params string[] expected)
    {
        var actual =
            element.EnumerateObject()
                .Select(
                    property =>
                        property.Name)
                .OrderBy(
                    name =>
                        name,
                    StringComparer.Ordinal)
                .ToArray();
        var sortedExpected =
            expected.OrderBy(
                    name =>
                        name,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            sortedExpected,
            actual);
    }

    private static JsonElement LoadJson(
        string path)
    {
        using var document =
            JsonDocument.Parse(
                File.ReadAllText(
                    path));

        return document.RootElement.Clone();
    }

    private static string GetSchemaRequestPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "schemas",
            "research",
            "earnings-transcript-facts-v1.schema-request.json");

    private static string GetProviderDemoPath(
        string fileName) =>
        Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "research",
            "provider-transcript-demo",
            fileName);

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
}
