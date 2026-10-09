using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.DocFlow;

/// <summary>Internal-network HTTP transport for the versioned DocFlow extraction API.</summary>
public sealed class DocFlowHttpExtractionAdapter : IDocFlowExtractionPort
{
    private const int MaximumResponseBytes = 2_097_152;
    private const int MaximumRequestBytes = 1_048_576;
    private readonly HttpClient _client;

    public DocFlowHttpExtractionAdapter(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ValidateBaseAddress(client.BaseAddress);
    }

    public async Task<DocFlowExtractionResult> ExtractAsync(
        DocFlowExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RawDocument.ValueKind != JsonValueKind.Object ||
            request.SchemaRequest.ValueKind != JsonValueKind.Object ||
            request.Provider is not ("openai" or "groq") ||
            string.IsNullOrWhiteSpace(request.Model) ||
            request.Model.Length > 200 ||
            request.DocumentName?.Length > 256)
            throw new ArgumentException("Invalid DocFlow extraction request.", nameof(request));

        var payload = new
        {
            schemaVersion = 1,
            rawDocument = request.RawDocument,
            schemaRequest = request.SchemaRequest,
            provider = request.Provider,
            model = request.Model,
            documentName = request.DocumentName
        };
        var requestBody = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (requestBody.Length > MaximumRequestBytes)
            throw new ArgumentException("DocFlow request exceeds maximum size.", nameof(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/extractions");
        message.Content = new ByteArrayContent(requestBody);
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DocFlowHttpExtractionException("DocFlow extraction service rejected the request.");

        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new DocFlowHttpExtractionException("DocFlow response exceeded the maximum size.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new DocFlowHttpExtractionException("DocFlow response exceeded the maximum size.");
            buffer.Write(chunk, 0, read);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetInt32() != 1 ||
                !root.TryGetProperty("normalizedDocument", out var normalized) ||
                normalized.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("structuredResult", out var structured) ||
                structured.ValueKind != JsonValueKind.Object ||
                !normalized.TryGetProperty("document_id", out _) ||
                !normalized.TryGetProperty("fingerprint", out _) ||
                !structured.TryGetProperty("validation_status", out _))
                throw new DocFlowHttpExtractionException("DocFlow response contract is invalid.");
            return new DocFlowExtractionResult(normalized.GetRawText(), structured.GetRawText());
        }
        catch (JsonException)
        {
            throw new DocFlowHttpExtractionException("DocFlow response is malformed.");
        }
    }

    private static void ValidateBaseAddress(Uri? address)
    {
        if (address is null || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0 ||
            !address.IsAbsoluteUri ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) ||
            (!address.IsLoopback && address.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("DocFlow base address must be loopback HTTP or HTTPS without embedded credentials.");
    }
}

public sealed class DocFlowHttpExtractionException(string message) : Exception(message);
