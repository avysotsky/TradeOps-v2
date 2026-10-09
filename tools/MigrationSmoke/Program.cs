using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Infrastructure.DocFlow;

// MIG-02: provider-free HTTP smoke against the real DocFlow Python ASGI application.
// Never pass exchange credentials, client documents or live LLM credentials.
const string endpoint = "http://127.0.0.1:18763/";
using var http = new HttpClient
{
    BaseAddress = new Uri(endpoint),
    Timeout = TimeSpan.FromSeconds(20)
};
var adapter = new DocFlowHttpExtractionAdapter(http);

using var raw = JsonDocument.Parse("""
    {
      "title": "Sample note",
      "document_type": "generic",
      "source": {
        "provider": "synthetic",
        "source_document_id": "mig02-sample-1",
        "source_timestamp": "2026-10-01T10:00:00Z",
        "published_at": "2026-10-01T10:01:00Z",
        "retrieved_at": "2026-10-01T10:02:00Z"
      },
      "participants": [],
      "segments": [{"sequence": 1, "text": "Example text."}]
    }
    """);

using var schema = JsonDocument.Parse("""
    {
      "schema_name": "sample",
      "schema_version": 1,
      "json_schema": {
        "type": "object",
        "additionalProperties": false,
        "required": ["category"],
        "properties": {"category": {"type": "string"}}
      }
    }
    """);

var request = new DocFlowExtractionRequest(
    raw.RootElement.Clone(), schema.RootElement.Clone(), "groq", "model-test");

var first = await adapter.ExtractAsync(request);
var second = await adapter.ExtractAsync(request);
using var doc = JsonDocument.Parse(first.NormalizedDocumentJson);
using var facts = JsonDocument.Parse(first.StructuredExtractionJson);
var normalized = doc.RootElement;
var structured = facts.RootElement;

static void Require(bool value, string error)
{
    if (!value) throw new InvalidOperationException(error);
}

var docId = normalized.GetProperty("document_id").GetString();
var fingerprint = normalized.GetProperty("fingerprint").GetString();
var segments = normalized.GetProperty("segments");
var segmentId = segments[0].GetProperty("segment_id").GetString();
Require(docId is not null && docId.StartsWith("textdoc:", StringComparison.Ordinal) && docId.Length == 72,
    "DocFlow document ID violated canonical SHA-256 contract.");
Require(fingerprint is not null && fingerprint.Length == 64,
    "DocFlow fingerprint violated canonical SHA-256 contract.");
Require(segments.GetArrayLength() == 1 &&
        segmentId is not null && segmentId.StartsWith("textseg:", StringComparison.Ordinal) && segmentId.Length == 72,
    "DocFlow segment ID violated canonical SHA-256 contract.");
Require(string.Equals(first.NormalizedDocumentJson, second.NormalizedDocumentJson, StringComparison.Ordinal) &&
        string.Equals(first.StructuredExtractionJson, second.StructuredExtractionJson, StringComparison.Ordinal),
    "DocFlow synthetic contract is not deterministic.");
Require(structured.GetProperty("data").GetProperty("category").GetString() == "generic",
    "DocFlow structured facts were not returned.");
Require(structured.GetProperty("validation_status").GetString() == "valid",
    "DocFlow synthetic structured validation did not pass.");

using var invalidResponse = await http.PostAsJsonAsync("api/v1/extractions", new
{
    schemaVersion = 2,
    rawDocument = raw.RootElement,
    schemaRequest = schema.RootElement,
    provider = "groq",
    model = "model-test"
});
Require(invalidResponse.StatusCode == HttpStatusCode.UnprocessableEntity,
    "DocFlow HTTP schemaVersion mismatch must fail closed (422).");
var rejection = await invalidResponse.Content.ReadAsStringAsync();
Require(!rejection.Contains("Example text.", StringComparison.Ordinal),
    "Invalid DocFlow response leaked source text.");

Console.WriteLine("MIG02 PASS: real DocFlow ASGI HTTP → TradeOps .NET application port");
Console.WriteLine("MIG02 PASS: canonical SHA-256 document/segment identities and deterministic structured facts");
Console.WriteLine("MIG02 PASS: invalid schemaVersion rejected with sanitized 422");
