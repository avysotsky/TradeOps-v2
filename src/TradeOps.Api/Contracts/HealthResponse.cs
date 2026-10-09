namespace TradeOps.Api.Contracts;

public sealed record HealthResponse(
    string Status,
    IReadOnlyDictionary<string, string>? Dependencies = null);
