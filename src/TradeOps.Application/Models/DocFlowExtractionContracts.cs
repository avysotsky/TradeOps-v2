using System.Text.Json;

namespace TradeOps.Application.Models;

public sealed record DocFlowExtractionRequest(
    JsonElement RawDocument,
    JsonElement SchemaRequest,
    string Provider,
    string Model,
    string? DocumentName = null);

public sealed record DocFlowExtractionResult(
    string NormalizedDocumentJson,
    string StructuredExtractionJson);

public interface IDocFlowExtractionPort
{
    Task<DocFlowExtractionResult> ExtractAsync(
        DocFlowExtractionRequest request,
        CancellationToken cancellationToken = default);
}
