namespace TradeOps.Application.Models;

public sealed record RiskDecision(bool IsAllowed, IReadOnlyCollection<string> Reasons)
{
    public static RiskDecision Allowed() => new(true, Array.Empty<string>());

    public static RiskDecision Rejected(params string[] reasons) => new(false, reasons);
}
