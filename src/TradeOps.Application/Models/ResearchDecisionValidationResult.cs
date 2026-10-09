namespace TradeOps.Application.Models;

public sealed record ResearchDecisionValidationResult(
    ResearchDecision? NormalizedDecision,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
