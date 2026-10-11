using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocFlow.Extraction.Application;
using DocFlow.Normalization;
using DocFlow.SchemaExtraction;
using TradeOps.Application.Models;

// Standalone synthetic test fixture, compiled into an external disposable project.
// This deliberately does NOT implement or register IDocFlowExtractionPort.
internal static class Program
{
    private const string Raw = """
        {"title":" Contract ","document_type":"generic","source":{"provider":"synthetic","source_document_id":"tradeops-fixture-42","source_timestamp":"2026-10-09T11:00:00+03:00"},"participants":[{"participant_id":"p1","display_name":" Alice "}],"segments":[{"sequence":2,"participant_id":"p1","text":" Second "},{"sequence":1,"participant_id":"p1","text":" First "}]}
        """;
    private const string Schema = """
        {"schema_name":"classification","schema_version":1,"json_schema":{"type":"object","required":["category"],"properties":{"category":{"type":"string"}},"additionalProperties":false}}
        """;
    private static int _assertions;
    private static int _cases;

    public static async Task<int> Main()
    {
        try
        {
            await Case("ABI-01-source-assembly-boundary", AssemblyBoundary);
            await Case("ABI-02-raw-mapping-and-canonical-artifacts", RawMapping);
            await Case("ABI-03-owned-json-lifetime", Ownership);
            await Case("ABI-04-pre-cancellation", PreCancellation);
            await Case("ABI-05-selector-rejection", SelectorRejection);
            await Case("ABI-06-malformed-input-rejection", MalformedInput);
            await Case("ABI-07-invalid-structured-result", InvalidStructured);
            Console.WriteLine($"TO-V2-05 TOTAL cases={_cases} passed={_cases} failed=0 assertions={_assertions}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("TO-V2-05 SYNTHETIC FAIL: " + error);
            return 2;
        }
    }

    private static async Task Case(string name, Func<Task> action)
    {
        await action();
        _cases++;
        Console.WriteLine("PASS " + name);
    }
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + reason);
        Interlocked.Increment(ref _assertions);
    }
    private static DocFlowExtractionRequest Request(string provider = "synthetic", string model = "model-a",
        JsonElement? raw = null, JsonElement? schema = null) =>
        new(raw ?? Json(Raw), schema ?? Json(Schema), provider, model, "fixture-document");

    // Research-only mapping; there is no runtime port implementation or DI registration.
    private static async Task<DocFlowExtractionResult> Map(
        IDocFlowExtractionFacade facade, DocFlowExtractionRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var schema = SchemaExtractionRequest.Parse(request.SchemaRequest.Clone());
        var raw = new RawExtractionRequest(request.RawDocument.Clone(), schema,
            new BackendSelection(request.Provider, request.Model), request.DocumentName);
        var artifacts = await facade.ExtractRawAsync(raw, token);
        token.ThrowIfCancellationRequested();
        return new(artifacts.NormalizedDocument.GetRawText(), artifacts.StructuredResult.GetRawText());
    }

    private static Task AssemblyBoundary()
    {
        var application = typeof(IDocFlowExtractionPort).Assembly;
        var facade = typeof(DocFlowExtractionFacade).Assembly;
        Check(application.GetName().Name == "TradeOps.Application", "port from TradeOps source");
        Check(facade.GetName().Name == "DocFlow.Extraction.Application", "actual facade assembly");
        Check(typeof(TextDocumentNormalizer).Assembly.GetName().Name == "DocFlow.Normalization", "normalizer assembly");
        Check(typeof(SchemaExtractionRequest).Assembly.GetName().Name == "DocFlow.SchemaExtraction", "schema assembly");
        var a = application.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Check(a.All(x => x is null || !x.StartsWith("DocFlow.", StringComparison.Ordinal)),
            "TradeOps.Application contains no DocFlow reference");
        var d = facade.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Check(d.Contains("DocFlow.Normalization") && d.Contains("DocFlow.SchemaExtraction"),
            "facade uses correct DocFlow dependency graph");
        Check(d.All(x => x is null || !x.StartsWith("TradeOps", StringComparison.Ordinal)),
            "DocFlow packages contain no TradeOps dependency");
        Check(typeof(IDocFlowExtractionPort).IsInterface &&
              typeof(IDocFlowExtractionPort).GetMethod("ExtractAsync")?.ReturnType ==
                  typeof(Task<DocFlowExtractionResult>), "real unchanged port ABI");
        Check(!application.GetTypes().Any(t => t.Name.Contains("InProcessDocFlow", StringComparison.Ordinal)),
            "no production in-process port added");
        return Task.CompletedTask;
    }

    private static async Task RawMapping()
    {
        var factory = new FakeFactory();
        var request = Request();
        var beforeRaw = request.RawDocument.GetRawText();
        var beforeSchema = request.SchemaRequest.GetRawText();
        var result = await Map(new DocFlowExtractionFacade(factory), request);
        using var doc = JsonDocument.Parse(result.NormalizedDocumentJson);
        using var data = JsonDocument.Parse(result.StructuredExtractionJson);
        var expected = TextDocumentNormalizer.Normalize(request.RawDocument).ToJsonString();
        Check(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(result.NormalizedDocumentJson)),
            "canonical JSON equals independently calculated library normalization");
        Check(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result.NormalizedDocumentJson)) ==
              result.NormalizedDocumentJson, "normalized lossless UTF-8");
        Check(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result.StructuredExtractionJson)) ==
              result.StructuredExtractionJson, "structured lossless UTF-8");
        Check(doc.RootElement.GetProperty("document_id").GetString()!.StartsWith("textdoc:", StringComparison.Ordinal),
            "canonical DocFlow document_id");
        var fingerprint = doc.RootElement.GetProperty("fingerprint").GetString()!;
        Check(fingerprint.Length == 64, "fingerprint 64 characters");
        Check(fingerprint.All(c => "0123456789abcdef".Contains(c)), "lowercase SHA-256 fingerprint");
        Check(doc.RootElement.GetProperty("source").GetProperty("source_document_id").GetString() ==
              "tradeops-fixture-42", "source provenance");
        Check(doc.RootElement.GetProperty("segments")[0].GetProperty("sequence").GetInt32() == 1,
            "canonical ordered segments");
        Check(doc.RootElement.GetProperty("segments")[0].GetProperty("segment_id").GetString()!
              .StartsWith("textseg:", StringComparison.Ordinal), "canonical segment id");
        Check(data.RootElement.GetProperty("engine").GetString() ==
              "schema_driven_text_v1:tradeops_fixture", "real result factory engine");
        Check(data.RootElement.GetProperty("data").GetProperty("category").GetString() ==
              "analysis", "native result data");
        Check(data.RootElement.GetProperty("validation_status").GetString() == "valid",
            "schema validation state");
        Check(data.RootElement.GetProperty("validation").GetProperty("status").GetString() == "valid",
            "nested schema validation state");
        Check(factory.Created == 1 && factory.Backend!.Calls == 1,
            "exactly one backend factory create and invocation");
        Check(factory.Backend!.SchemaName == "classification" && factory.Backend!.SchemaVersion == 1,
            "schema identity passed through");
        Check(factory.Backend!.DocumentName == "fixture-document", "document name passed through");
        Check(request.RawDocument.GetRawText() == beforeRaw &&
              request.SchemaRequest.GetRawText() == beforeSchema, "caller input not mutated");
    }

    private static async Task Ownership()
    {
        var factory = new FakeFactory();
        DocFlowExtractionResult result;
        using (var raw = JsonDocument.Parse(Raw))
        using (var schema = JsonDocument.Parse(Schema))
        {
            var request = new DocFlowExtractionRequest(raw.RootElement, schema.RootElement,
                "synthetic", "model-a");
            result = await Map(new DocFlowExtractionFacade(factory), request);
        }
        using var norm = JsonDocument.Parse(result.NormalizedDocumentJson);
        using var structured = JsonDocument.Parse(result.StructuredExtractionJson);
        Check(norm.RootElement.GetProperty("document_id").GetString()!.StartsWith("textdoc:", StringComparison.Ordinal),
            "normalized artifact survives source JsonDocument disposal");
        Check(structured.RootElement.GetProperty("data").GetProperty("category").GetString() ==
              "analysis", "structured artifact survives source disposal");
        Check(factory.Created == 1 && factory.Backend!.Calls == 1, "owned artifacts dispatched exactly once");
    }

    private static async Task PreCancellation()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        var factory = new FakeFactory();
        await Reject<OperationCanceledException>(
            () => Map(new DocFlowExtractionFacade(factory), Request(), token.Token), "pre-cancel rejected");
        Check(factory.Created == 0, "pre-cancel no factory creation");
        Check(factory.Backend is null, "pre-cancel no backend dispatch");
    }

    private static async Task SelectorRejection()
    {
        foreach (var (provider, model) in new[] {
            ("external", "model-a"), ("synthetic", "unapproved"),
            ("", "model-a"), ("synthetic", "bad\nmodel")
        })
        {
            var factory = new FakeFactory();
            await Reject<ArgumentException>(
                () => Map(new DocFlowExtractionFacade(factory), Request(provider, model)),
                "invalid/unsupported selector");
            Check(factory.Created == 0, "bad selector no factory create");
            Check(factory.Backend is null, "bad selector no backend call");
        }
    }

    private static async Task MalformedInput()
    {
        foreach (var raw in new[] {
            """{"title":"bad","document_type":"generic","source":{"provider":"synthetic","source_document_id":"x"},"segments":[]}""",
            """{"title":"bad","document_type":"generic","source":{"provider":"synthetic","source_document_id":"x"},"segments":[{"sequence":1,"text":"hello"}],"document_id":"forged"}"""
        })
        {
            var factory = new FakeFactory();
            await Reject<ArgumentException>(
                () => Map(new DocFlowExtractionFacade(factory), Request(raw: Json(raw))), "invalid raw");
            Check(factory.Created == 0 && factory.Backend is null, "bad raw no backend");
        }
        foreach (var schema in new[] {
            """{"schema_name":"test","schema_version":0,"json_schema":{"type":"object"}}""",
            """{"schema_name":"a","schema_name":"b","schema_version":1,"json_schema":{"type":"object"}}""",
            """{"schema_name":"test","schema_version":1,"json_schema":{"type":"array"}}"""
        })
        {
            var factory = new FakeFactory();
            await Reject<ArgumentException>(
                () => Map(new DocFlowExtractionFacade(factory), Request(schema: Json(schema))), "bad schema");
            Check(factory.Created == 0, "bad schema no backend");
        }
    }

    private static async Task InvalidStructured()
    {
        var factory = new FakeFactory("""{"unknown":1}""");
        var result = await Map(new DocFlowExtractionFacade(factory), Request());
        using var structured = JsonDocument.Parse(result.StructuredExtractionJson);
        var root = structured.RootElement;
        Check(root.GetProperty("validation_status").GetString() == "invalid",
            "invalid schema result not silently accepted");
        Check(root.GetProperty("validation").GetProperty("checks").GetProperty("json_schema")
              .GetProperty("status").GetString() == "failed", "native diagnostics");
        Check(root.GetProperty("data").GetProperty("unknown").GetInt32() == 1,
            "original invalid data retained");
        Check(factory.Created == 1 && factory.Backend!.Calls == 1, "no retry/extra call");
    }

    private static async Task Reject<T>(Func<Task> action, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, description); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + description);
    }

    private sealed class FakeFactory : IExtractionBackendFactory
    {
        private readonly string _response;
        public FakeFactory(string response = """{"category":"analysis"}""") => _response = response;
        public int Created { get; private set; }
        public FakeBackend? Backend { get; private set; }
        public bool IsSupported(BackendSelection selector) =>
            selector.Provider == "synthetic" && selector.Model == "model-a";
        public IExtractionBackend Create(BackendSelection selector)
        {
            Created++;
            Backend = new FakeBackend(_response);
            return Backend;
        }
    }
    private sealed class FakeBackend : IRequestAwareExtractionBackend
    {
        private readonly JsonElement _result;
        public FakeBackend(string json) => _result = Json(json);
        public string Name => "tradeops_fixture";
        public int Calls { get; private set; }
        public string? SchemaName { get; private set; }
        public int SchemaVersion { get; private set; }
        public string? DocumentName { get; private set; }
        public Task<JsonElement> ExtractAsync(JsonElement doc, SchemaExtractionRequest schema,
            string? documentName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            SchemaName = schema.SchemaName;
            SchemaVersion = schema.SchemaVersion;
            DocumentName = documentName;
            return Task.FromResult(_result.Clone());
        }
        public Task<JsonElement> ExtractAsync(JsonElement doc, JsonElement schema, CancellationToken token) =>
            throw new InvalidOperationException("Legacy non-request-aware extraction must not be used");
    }
}
