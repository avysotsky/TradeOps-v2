using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Infrastructure.DocFlow;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DocFlowHttpExtractionAdapterTests
{
    private static DocFlowExtractionRequest Request() => new(
        JsonDocument.Parse("""{"title":"Sample note","segments":[]}""").RootElement.Clone(),
        JsonDocument.Parse("""{"schema_name":"sample","schema_version":1,"json_schema":{"type":"object"}}""").RootElement.Clone(),
        "groq", "model-test", "sample.txt");

    private const string GoodResponse = """
        {"schemaVersion":1,"normalizedDocument":{"document_id":"textdoc:sample","fingerprint":"abc","segments":[]},"structuredResult":{"engine":"test","validation_status":"valid","data":{"category":"example"}}}
        """;

    [Fact]
    public async Task ValidResponsePreservesCanonicalJsonIdentifiers()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GoodResponse, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8081/") };
        var result = await new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request());
        Assert.Equal("textdoc:sample", JsonDocument.Parse(result.NormalizedDocumentJson)
            .RootElement.GetProperty("document_id").GetString());
        Assert.Equal("example", JsonDocument.Parse(result.StructuredExtractionJson)
            .RootElement.GetProperty("data").GetProperty("category").GetString());
        Assert.Equal("/api/v1/extractions", handler.LastUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.NotNull(handler.Body);
        Assert.Equal(1, JsonDocument.Parse(handler.Body!).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain("apiKey", handler.Body!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"normalizedDocument":{},"structuredResult":{}}""")]
    [InlineData("""{"schemaVersion":1,"normalizedDocument":[],"structuredResult":{}}""")]
    [InlineData("""{"schemaVersion":1,"normalizedDocument":{"document_id":"a"},"structuredResult":{}}""")]
    [InlineData("""not json""")]
    public async Task InvalidOrUnsupportedResponseFailsClosed(string responseBody)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody)
        })) { BaseAddress = new Uri("http://localhost:8081/") };
        await Assert.ThrowsAsync<DocFlowHttpExtractionException>(() =>
            new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request()));
    }

    [Fact]
    public async Task ProviderErrorIsSanitized()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("secret-provider-payload")
        })) { BaseAddress = new Uri("http://localhost:8081/") };
        var ex = await Assert.ThrowsAsync<DocFlowHttpExtractionException>(() =>
            new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request()));
        Assert.DoesNotContain("secret-provider-payload", ex.Message);
    }

    [Fact]
    public async Task ResponseSizeIsBoundedWhenContentLengthIsAbsent()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2_100_000))
        })) { BaseAddress = new Uri("http://localhost:8081/") };
        await Assert.ThrowsAsync<DocFlowHttpExtractionException>(() =>
            new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request()));
    }

    [Fact]
    public void UntrustedRemoteHttpAndCredentialsAreRejected()
    {
        foreach (var url in new[] { "http://example.com/", "http://127.0.0.1:1234@other.example/", "file:///tmp/doc", "https://user:pass@example.com/" })
        {
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Throws<ArgumentException>(() => new DocFlowHttpExtractionAdapter(client));
        }
    }

    [Fact]
    public async Task InvalidProviderRejectedBeforeTransportCall()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8081/") };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request() with { Provider = "other" }));
    }

    [Fact]
    public async Task CancellationIsPropagatedWithoutNetwork()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Cancelled request must not send"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8081/") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DocFlowHttpExtractionAdapter(client).ExtractAsync(Request(), cts.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return handle(request);
        }
    }
}
