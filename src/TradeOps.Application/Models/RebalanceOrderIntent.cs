using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

/// <summary>
/// A broker-neutral portfolio action produced before TradeOps risk approval.
/// It is not an executable order and must pass the downstream risk/emergency-stop boundary.
/// </summary>
public sealed record RebalanceOrderIntent(
    string DecisionId,
    InstrumentReference Instrument,
    OrderSide Side,
    decimal Quantity,
    decimal ReferencePrice,
    decimal EstimatedNotional)
{
    public bool RequiresRiskApproval => true;
}
