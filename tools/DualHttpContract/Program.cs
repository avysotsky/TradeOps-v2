// TO-V2-03: .NET 10 source-linked, in-memory only; see accompanying validator.
// No TradeOps API/Worker, sockets, brokers, real OIDC, database or provider processes.
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocFlow.Extraction.Application;
using DocFlow.Infrastructure.Persistence;
using DocFlow.SchemaExtraction;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TradeOps.Application.Models;
using TradeOps.Infrastructure.DocFlow;

internal static class DualHttpContract
{
    private const string Issuer = "https://local-issuer.invalid";
    private const string Audience = "docflow-offline";
    private const string Route = "/api/v1/extractions";
    private const string Sentinel = "PROVIDER_PRIVATE_SENTINEL_DO_NOT_EMIT";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes(
        "to-v2-03-isolated-jwt-signing-key-for-testing-only-1234567890"));

    private const string Raw = """
        {"title":"Звіт – déjà vu 🚀","document_type":"generic","source":{"provider":"synthetic","source_document_id":"ñ-123","source_timestamp":"2026-10-01T10:00:00Z","published_at":"2026-10-01T10:01:00Z","retrieved_at":"2026-10-01T10:02:00Z"},"participants":[{"participant_id":"person-1","display_name":"Олена Élodie","role":"speaker","organization":"Тест"}],"segments":[{"sequence":2,"participant_id":"person-1","text":"Друга частина — résumé 🚀"},{"sequence":1,"participant_id":"person-1","text":"Привіт, München! | поле"}]}
        """;
    private const string Schema = """
        {"schema_name":"unicode-sample","schema_version":1,"json_schema":{"type":"object","additionalProperties":false,"required":["category"],"properties":{"category":{"type":"string"}}}}
        """;
    private static readonly (string Name, Func<Task> Run)[] Cases =
    {
        ("canonical-native-parity-utf8", CanonicalParity),
        ("invalid-provider-data", InvalidProviderData),
        ("jwt-security-https", JwtSecurity),
        ("request-contract-and-bounds", RequestContractAndBounds),
        ("http-response-and-bounds", HttpResponseAndBounds),
        ("backend-errors-and-routing", BackendErrorsAndRouting),
        ("deadline-and-cancellation", DeadlineAndCancellation),
        ("bounded-concurrency", BoundedConcurrency)
    };
    private static int assertions;

    public static async Task<int> Main()
    {
        var passed = 0;
        var failed = 0;
        foreach (var (name, run) in Cases)
        {
            try { await run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex)
            {
                failed++;
                // No raw requests or token values are printed by test helpers.
                Console.Error.WriteLine("FAIL " + name + " " + ex.GetType().Name +
                                        ": " + (ex is CheckFailure ? ex.Message : "unexpected test exception"));
            }
        }
        Console.WriteLine($"TO-V2-03 TOTAL cases={Cases.Length} passed={passed} failed={failed} assertions={assertions}");
        return failed == 0 ? 0 : 1;
    }

    private sealed class CheckFailure(string message) : Exception(message) { }

    private static void Check(bool value, string label)
    {
        Interlocked.Increment(ref assertions);
        if (!value) throw new CheckFailure(label);
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static DocFlowExtractionRequest Input(JsonElement? raw = null,
        JsonElement? schema = null, string provider = "groq", string model = "model-test",
        string? name = "Résumé – звіт") =>
        new(raw ?? Json(Raw), schema ?? Json(Schema), provider, model, name);

    private static string Wire(DocFlowExtractionRequest x, int version = 1) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = version, rawDocument = x.RawDocument,
            schemaRequest = x.SchemaRequest, provider = x.Provider,
            model = x.Model, documentName = x.DocumentName
        });

    private static string Token(string? scope = "docflow.extract",
        bool client = true, string issuer = Issuer, string audience = Audience,
        bool expired = false, SecurityKey? key = null, string clientId = "tradeops-integration-test")
    {
        var claims = new List<Claim>();
        if (scope != null) claims.Add(new Claim("scope", scope));
        if (client) claims.Add(new Claim("client_id", clientId));
        var jwt = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(expired ? -10 : -1),
            expires: DateTime.UtcNow.AddMinutes(expired ? -5 : 5),
            signingCredentials: new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static HttpClient Client(Host host, string? jwt = null, bool https = true)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(https ? "https://localhost/" : "http://localhost/"),
            AllowAutoRedirect = false
        });
        if (jwt != null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static DocFlowHttpExtractionAdapter Adapter(HttpClient client) => new(client);

    private static async Task<HttpResponseMessage> RawPost(HttpClient client, string raw,
        string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(raw));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task Status(HttpResponseMessage response, int expected, string? code = null)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Check((int)response.StatusCode == expected, "HTTP status mismatch: " + expected);
            if (code != null)
                Check(body == "{\"error\":\"" + code + "\"}", "safe HTTP error: " + code);
            Check(!body.Contains(Sentinel, StringComparison.Ordinal), "no provider secret in HTTP response");
        }
    }

    private static async Task Sanitized(Func<Task> call, string label)
    {
        try { await call(); throw new CheckFailure("expected rejection: " + label); }
        catch (DocFlowHttpExtractionException e)
        {
            Check(!e.Message.Contains(Sentinel, StringComparison.Ordinal), label + " sanitized");
            Check(!e.Message.Contains("Привіт", StringComparison.Ordinal), label + " no document");
            Check(!e.Message.Contains("eyJ", StringComparison.Ordinal), label + " no JWT");
        }
    }

    private static bool Same(JsonElement x, JsonElement y) =>
        JsonNode.DeepEquals(JsonNode.Parse(x.GetRawText()), JsonNode.Parse(y.GetRawText()));

    private static async Task CanonicalParity()
    {
        var backend = new DeterministicFactory();
        using var host = new Host(backend);
        using var client = Client(host, Token());
        var actual = await Adapter(client).ExtractAsync(Input());
        using var directFactory = new DeterministicFactory();
        var direct = await new DocFlowExtractionFacade(directFactory).ExtractRawAsync(
            new RawExtractionRequest(Json(Raw), SchemaExtractionRequest.Parse(Json(Schema)),
                new BackendSelection("groq", "model-test"), "Résumé – звіт"));
        var normalized = Json(actual.NormalizedDocumentJson);
        var structured = Json(actual.StructuredExtractionJson);

        Check(Same(normalized, direct.NormalizedDocument), "full canonical normalized equality");
        Check(Same(structured, direct.StructuredResult), "full native structured equality");
        Check(normalized.GetProperty("document_id").GetString()!.StartsWith("textdoc:"), "document identity");
        Check(normalized.GetProperty("fingerprint").GetString()!.Length == 64, "fingerprint");
        var parts = normalized.GetProperty("participants");
        Check(parts.GetArrayLength() == 1 &&
              parts[0].GetProperty("display_name").GetString() == "Олена Élodie", "participant parity");
        var segments = normalized.GetProperty("segments");
        Check(segments.GetArrayLength() == 2, "segment count");
        Check(segments[0].GetProperty("sequence").GetInt32() == 1, "canonical segment ordering");
        Check(segments[0].GetProperty("segment_id").GetString()!.StartsWith("textseg:"), "segment id");
        Check(segments[1].GetProperty("text").GetString()!.Contains("🚀"), "unicode survives wire");
        Check(normalized.GetProperty("source").GetProperty("source_document_id").GetString() == "ñ-123",
            "provenance survives wire");
        Check(structured.GetProperty("validation_status").GetString() == "valid", "valid extraction");
        Check(structured.GetProperty("confidence").GetDouble() == 0.42, "confidence");
        Check(structured.GetProperty("data").GetProperty("category").GetString() == "generic", "data");
        Check(backend.Created == 1 && directFactory.Created == 1, "one backend each");
        Check(!Directory.EnumerateFileSystemEntries(host.StorageRoot).Any(), "no stored document");
    }

    private static async Task InvalidProviderData()
    {
        var backend = new DeterministicFactory { Data = Json("""{"unapproved":true}""") };
        using var host = new Host(backend);
        using var client = Client(host, Token());
        var result = await Adapter(client).ExtractAsync(Input());
        var structured = Json(result.StructuredExtractionJson);
        using var directFactory = new DeterministicFactory { Data = Json("""{"unapproved":true}""") };
        var direct = await new DocFlowExtractionFacade(directFactory).ExtractRawAsync(
            new RawExtractionRequest(Json(Raw), SchemaExtractionRequest.Parse(Json(Schema)),
                new BackendSelection("groq", "model-test"), "Résumé – звіт"));
        Check(Same(structured, direct.StructuredResult), "schema invalid complete parity");
        Check(structured.GetProperty("validation_status").GetString() == "invalid",
            "well formed but schema invalid data stays invalid");
        Check(backend.Created == 1, "schema invalid invoked once");
    }

    private static async Task JwtSecurity()
    {
        var factory = new DeterministicFactory();
        using var host = new Host(factory);
        using var client = Client(host);
        await Status(await RawPost(client, Wire(Input())), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), "spoofed.header.signature"), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), Token(issuer: "https://different.invalid")), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), Token(audience: "wrong")), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), Token(expired: true)), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), Token(key: new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("wrong-key-for-local-signature-verification-tests-12345678")))), 401, "unauthorized");
        await Status(await RawPost(client, Wire(Input()), Token(scope: "wrong.scope")), 403, "forbidden");
        await Status(await RawPost(client, Wire(Input()), Token(client: false)), 403, "forbidden");
        Check(factory.Created == 0, "reject JWT before backend");
        await Sanitized(() => Adapter(client).ExtractAsync(Input()), "adapter rejected missing jwt");
        using var plaintext = Client(host, Token(), https: false);
        await Status(await RawPost(plaintext, Wire(Input())), 403, "forbidden");
        Check(factory.Created == 0, "reject plaintext before backend");
        using var authorized = Client(host, Token());
        await Adapter(authorized).ExtractAsync(Input());
        Check(factory.Created == 1, "authorized request dispatches once");
        // EXPECTED SECURITY BLOCKER until DocFlow.Api pins a trusted client identity.
        // Current native policy only requires a nonempty client_id and may return 200.
        await Status(await RawPost(authorized, Wire(Input()),
            Token(clientId: "unapproved-service-client")), 403, "forbidden");
        Check(factory.Created == 1, "unapproved service identity rejected before dispatch");
    }

    private static async Task RequestContractAndBounds()
    {
        var factory = new DeterministicFactory();
        using var host = new Host(factory);
        using var client = Client(host, Token());
        await Status(await RawPost(client, Wire(Input(), 2)), 422, "invalid_request");
        await Status(await RawPost(client, Wire(Input()).Replace(
            "\"model\":\"model-test\"", "\"model\":\"model-test\",\"unknown\":true")),
            422, "invalid_request");
        await Status(await RawPost(client, Wire(Input()).Replace(
            "\"type\":\"string\"", "\"type\":\"string\",\"pattern\":\"bad\"")),
            422, "unsupported_schema");
        await Status(await RawPost(client, Wire(Input(model: "unapproved"))), 503, "provider_unavailable");
        await Sanitized(() => Adapter(client).ExtractAsync(Input(model: "unapproved")), "provider selection");
        await Status(await RawPost(client, Wire(Input()).Replace(
            "\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")),
            422, "invalid_request");
        await Status(await RawPost(client, Wire(Input(raw: Json(Raw.Replace(
            "\"title\":\"Звіт – déjà vu 🚀\"", "\"title\":null"))))), 422, "invalid_request");
        Check(factory.Created == 0, "bad schemas and selections never dispatch");
        await Sanitized(() => Adapter(client).ExtractAsync(Input(
            raw: Json("""{"invalid":"document"}""")), "normalization error");

        // The adapter rejects oversized UTF-8 payloads before any SendAsync call.
        var replacement = new string('x', 1_048_577);
        var rawLarge = Json(Raw.Replace("Привіт, München! | поле", replacement));
        var oversized = Input(raw: rawLarge);
        Check(Encoding.UTF8.GetByteCount(Wire(oversized)) > 1_048_576, "oversized request fixture");
        try
        {
            await Adapter(client).ExtractAsync(oversized);
            throw new CheckFailure("oversized adapter request was sent");
        }
        catch (ArgumentException e)
        {
            Check(e.Message.Contains("maximum size", StringComparison.Ordinal), "client-side request cap");
        }
        Check(factory.Created == 0, "client-side rejection before backend dispatch");
        await Status(await RawPost(client, Wire(oversized)), 413, "request_too_large");
    }

    private static async Task HttpResponseAndBounds()
    {
        var backend = new DeterministicFactory();
        using var host = new Host(backend);
        using var client = Client(host, Token());
        var hugeData = Json("{\"category\":\"" + new string('y', 2_200_000) + "\"}");
        backend.Data = hugeData;
        await Status(await RawPost(client, Wire(Input())), 502, "extraction_failed");
        await Sanitized(() => Adapter(client).ExtractAsync(Input()), "host output cap");
        Check(backend.Created == 2, "no output retry");

        // Edge-only adverse responses exercise the genuine TradeOps adapter's local
        // 2 MiB read/parse/contract gates. They are not claimed as native-host parity.
        foreach (var (name, body) in new (string, string)[]
        {
            ("version", """{"schemaVersion":2,"normalizedDocument":{},"structuredResult":{}}"""),
            ("malformed", """{"schemaVersion":"""),
            ("missing-field", """{"schemaVersion":1,"normalizedDocument":{},"structuredResult":{}}"""),
            ("oversized", new string('y', 2_097_153))
        })
        {
            using var c = EdgeClient(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
            await Sanitized(() => Adapter(c).ExtractAsync(Input()), name);
        }
        using var errorClient = EdgeClient(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(Sentinel)
        });
        await Sanitized(() => Adapter(errorClient).ExtractAsync(Input()), "bounded HTTP 502 message");
    }

    private static HttpClient EdgeClient(HttpResponseMessage response) =>
        new(new StaticHandler(response)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class StaticHandler(HttpResponseMessage result) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private static async Task BackendErrorsAndRouting()
    {
        var backend = new DeterministicFactory { Failure = new InvalidOperationException(Sentinel) };
        using var host = new Host(backend);
        using var client = Client(host, Token());
        await Status(await RawPost(client, Wire(Input())), 502, "extraction_failed");
        await Sanitized(() => Adapter(client).ExtractAsync(Input()), "provider failure");
        backend.Failure = new HttpRequestException(Sentinel);
        await Status(await RawPost(client, Wire(Input())), 503, "transient_failure");
        Check(backend.Created == 3, "one backend per request, no fallback");
        using var disabled = new Host(new DeterministicFactory(), enabled: false);
        using var disabledClient = Client(disabled, Token());
        await Status(await RawPost(disabledClient, Wire(Input())), 404);
        await Sanitized(() => Adapter(disabledClient).ExtractAsync(Input()), "route disabled");
        using var missing = new Host(null);
        using var missingClient = Client(missing, Token());
        await Status(await RawPost(missingClient, Wire(Input())), 503, "provider_unavailable");
        await Sanitized(() => Adapter(missingClient).ExtractAsync(Input()), "backend missing");
        Check(!Directory.EnumerateFileSystemEntries(host.StorageRoot).Any(), "no provider data stored");
    }

    private static async Task DeadlineAndCancellation()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DeterministicFactory
        {
            OnExtract = async token =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("""{"category":"never"}""");
            }
        };
        using var host = new Host(backend, deadline: 750);
        using var client = Client(host, Token());
        await Sanitized(() => Adapter(client).ExtractAsync(Input()), "server deadline");
        Check(backend.Created == 1, "deadline no retry");

        // Fresh instance so cancellation does not race the preceding deadline.
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new DeterministicFactory
        {
            OnExtract = async token =>
            {
                secondStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("""{"category":"never"}""");
            }
        };
        using var secondHost = new Host(second);
        using var secondClient = Client(secondHost, Token());
        using var abort = new CancellationTokenSource();
        var pending = Adapter(secondClient).ExtractAsync(Input(), abort.Token);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            throw new CheckFailure("cancelled extraction returned success");
        }
        catch (OperationCanceledException) { Check(true, "cancellation propagated"); }
        Check(second.Created == 1, "cancellation no retry");

        // Real adapter + HttpClient timeout, no infinite unbounded wait.
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new DeterministicFactory
        {
            OnExtract = async token =>
            {
                thirdStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("""{"category":"never"}""");
            }
        };
        using var thirdHost = new Host(third);
        using var thirdClient = Client(thirdHost, Token());
        thirdClient.Timeout = TimeSpan.FromMilliseconds(1500);
        var timed = Adapter(thirdClient).ExtractAsync(Input());
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await timed.WaitAsync(TimeSpan.FromSeconds(5));
            throw new CheckFailure("HttpClient timeout returned success");
        }
        catch (OperationCanceledException) { Check(true, "HttpClient timeout propagated"); }
        Check(third.Created == 1, "HttpClient timeout no retry");
    }

    private static async Task BoundedConcurrency()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DeterministicFactory
        {
            OnExtract = async token =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return Json("""{"category":"generic"}""");
            }
        };
        using var host = new Host(factory, concurrency: 1);
        using var client = Client(host, Token());
        var first = Adapter(client).ExtractAsync(Input());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Status(await RawPost(client, Wire(Input())), 429, "transient_failure");
        await Sanitized(() => Adapter(client).ExtractAsync(Input()), "concurrency 429");
        Check(factory.Created == 1, "429 before backend invocation");
        release.TrySetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check(factory.Created == 1, "no hidden retries");
    }

    private sealed class Host : WebApplicationFactory<global::Program>
    {
        private readonly IExtractionBackendFactory? backend;
        private readonly bool enabled;
        private readonly int deadline;
        private readonly int concurrency;
        public string StorageRoot { get; } =
            Path.Combine(Path.GetTempPath(), "to-v2-03-" + Guid.NewGuid().ToString("N"));

        public Host(IExtractionBackendFactory? backend, bool enabled = true,
            int deadline = 30_000, int concurrency = 4)
        {
            this.backend = backend;
            this.enabled = enabled;
            this.deadline = deadline;
            this.concurrency = concurrency;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var root = Environment.GetEnvironmentVariable("TO_V2_03_DOCFLOW_ROOT")
                ?? throw new InvalidOperationException("Missing test content root.");
            builder.UseContentRoot(Path.Combine(root, "src", "DocFlow.Api"));
            builder.UseEnvironment("Testing");
            // Minimal hosting reads builder.Configuration early: bootstrap + host config.
            builder.UseSetting("ExtractionHttp:Enabled", enabled.ToString());
            builder.UseSetting("ExtractionHttp:Authority", Issuer);
            builder.UseSetting("ExtractionHttp:Audience", Audience);
            builder.UseSetting("ExtractionHttp:AllowedSelections:0:Provider", "groq");
            builder.UseSetting("ExtractionHttp:AllowedSelections:0:Model", "model-test");
            builder.UseSetting("ExtractionHttp:DeadlineMilliseconds", deadline.ToString());
            builder.UseSetting("ExtractionHttp:MaxConcurrency", concurrency.ToString());
            builder.UseSetting("FileStorage:RootPath", StorageRoot);
            builder.UseSetting("ConnectionStrings:DocFlowDbContext",
                "Host=127.0.0.1;Port=1;Database=blocked;Username=nobody;Timeout=1");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ExtractionHttp:Enabled"] = enabled.ToString(),
                    ["ExtractionHttp:Authority"] = Issuer,
                    ["ExtractionHttp:Audience"] = Audience,
                    ["ExtractionHttp:AllowedSelections:0:Provider"] = "groq",
                    ["ExtractionHttp:AllowedSelections:0:Model"] = "model-test",
                    ["ExtractionHttp:DeadlineMilliseconds"] = deadline.ToString(),
                    ["ExtractionHttp:MaxConcurrency"] = concurrency.ToString(),
                    ["FileStorage:RootPath"] = StorageRoot,
                    ["ConnectionStrings:DocFlowDbContext"] =
                        "Host=127.0.0.1;Port=1;Database=blocked;Username=nobody;Timeout=1"
                }));
            builder.ConfigureTestServices(services =>
            {
                // DB resolution is forbidden in this fixture, not just configured to fail.
                services.RemoveAll<DocFlowDbContext>();
                services.RemoveAll<DbContextOptions<DocFlowDbContext>>();
                if (backend != null) services.AddSingleton(backend);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var oidc = new OpenIdConnectConfiguration { Issuer = Issuer };
                    oidc.SigningKeys.Add(Key);
                    options.Authority = null;
                    options.MetadataAddress = string.Empty;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(oidc);
                    options.Configuration = oidc;
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.IssuerSigningKey = Key;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(StorageRoot))
                Directory.Delete(StorageRoot, recursive: true);
        }
    }

    private sealed class DeterministicFactory : IExtractionBackendFactory
    {
        private int created;
        public int Created => Volatile.Read(ref created);
        public JsonElement Data { get; set; } = Json("""{"category":"generic"}""");
        public Exception? Failure { get; set; }
        public Func<CancellationToken, Task<JsonElement>>? OnExtract { get; set; }
        public bool IsSupported(BackendSelection selection) =>
            selection.Provider == "groq" && selection.Model == "model-test";
        public IExtractionBackend Create(BackendSelection selection)
        {
            Interlocked.Increment(ref created);
            return new Backend(this);
        }

        private sealed class Backend(DeterministicFactory parent) :
            IRequestAwareExtractionBackend, IConfidentExtractionBackend
        {
            public string Name => "offline_factory";
            public double? Confidence => 0.42;
            public Task<JsonElement> ExtractAsync(JsonElement document, JsonElement schema,
                CancellationToken cancellationToken) =>
                ExtractAsync(document, null!, null, cancellationToken);
            public async Task<JsonElement> ExtractAsync(JsonElement document,
                SchemaExtractionRequest request, string? documentName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parent.Failure != null) throw parent.Failure;
                if (parent.OnExtract != null) return await parent.OnExtract(cancellationToken);
                return parent.Data.Clone();
            }
        }
    }
}
