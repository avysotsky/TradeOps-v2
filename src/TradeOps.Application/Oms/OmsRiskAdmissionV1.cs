using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

// These inputs come from a trusted server-side policy/valuation port, never the order request.
// Amounts are conservative USD risk bounds, not native order quantities or generic RiskSettings.
public sealed record OmsAccountRiskPolicyV1(int SchemaVersion, AccountKey Scope, string Revision,
    decimal MaxOrderUsd, decimal MaxPositionDeltaUsd, decimal MaxPortfolioUsd,
    decimal MaxLeverage, decimal MaxMarginUsd, decimal MaxInstrumentUsd,
    decimal MaxDailyLossUsd, decimal MaxPendingUsd, int MaxPendingCommands);

public sealed record OmsRiskValuationV1(int SchemaVersion, AccountKey Scope, string StateSha256,
    string CommandSha256, DateTimeOffset ObservedAtUtc, DateTimeOffset ValidThroughUtc,
    bool InstrumentEligible, bool AccountEligible, bool UnitsVerified, bool ReduceOnlyVerified,
    decimal OrderNotionalUsd, decimal PositionDeltaUsd, decimal PortfolioGrossUsd,
    decimal InstrumentGrossUsd, decimal EquityUsd, decimal MarginUsedUsd,
    decimal IncrementalMarginUsd, decimal DailyLossUsd);

public sealed record OmsRiskInputsV1(OmsAccountRiskPolicyV1 Policy, OmsRiskValuationV1 Valuation);

public interface IOmsRiskInputsReader
{
    Task<OmsRiskInputsV1?> ReadAsync(OmsJournalCommandRequest command,
        OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default);
}

public enum OmsRiskAdmissionResult { Reserved = 1, Duplicate, Collision, Denied, StaleBaseline, StaleOrderVersion, ControlBlocked }

/// <summary>Conservative pending upper bounds: never net opposite orders and never release on ACK.
/// A future proven settlement path may retire reservations; v1 intentionally retains all of them.</summary>
public static class OmsRiskLimitsV1
{
    public static bool Allows(OmsJournalCommandRequest command, string stateSha, OmsRiskInputsV1 inputs,
        decimal pendingUsd, decimal pendingMarginUsd, long pendingCount, DateTimeOffset now)
    {
        var p = inputs.Policy; var v = inputs.Valuation;
        if (p.SchemaVersion != 1 || v.SchemaVersion != 1 || p.Scope != command.Scope || v.Scope != command.Scope ||
            string.IsNullOrWhiteSpace(p.Revision) || p.Revision.Length > 128 ||
            v.StateSha256 != stateSha || v.CommandSha256 != Convert.ToHexString(OmsJournalCanonical.EncodeCommand(command).Sha256) ||
            v.ObservedAtUtc.Offset != TimeSpan.Zero || v.ValidThroughUtc.Offset != TimeSpan.Zero ||
            now < v.ObservedAtUtc || now > v.ValidThroughUtc || v.ValidThroughUtc - v.ObservedAtUtc > TimeSpan.FromSeconds(5) ||
            !v.InstrumentEligible || !v.AccountEligible || !v.UnitsVerified || command.Payload.ReduceOnly && !v.ReduceOnlyVerified ||
            command.Payload.Kind != OmsJournalCommandKind.Place ||
            p.MaxOrderUsd <= 0 || p.MaxPositionDeltaUsd <= 0 || p.MaxPortfolioUsd <= 0 || p.MaxLeverage <= 0 ||
            p.MaxMarginUsd <= 0 || p.MaxInstrumentUsd <= 0 || p.MaxDailyLossUsd <= 0 || p.MaxPendingUsd <= 0 || p.MaxPendingCommands <= 0 ||
            v.OrderNotionalUsd <= 0 || v.PositionDeltaUsd < 0 || v.PortfolioGrossUsd < 0 || v.InstrumentGrossUsd < 0 ||
            v.EquityUsd <= 0 || v.MarginUsedUsd < 0 || v.IncrementalMarginUsd < 0 || v.DailyLossUsd < 0 ||
            pendingUsd < 0 || pendingMarginUsd < 0 || pendingCount < 0) return false;
        try
        {
            return v.OrderNotionalUsd <= p.MaxOrderUsd && v.PositionDeltaUsd + pendingUsd <= p.MaxPositionDeltaUsd &&
                v.PortfolioGrossUsd + pendingUsd + v.OrderNotionalUsd <= p.MaxPortfolioUsd &&
                v.PortfolioGrossUsd + pendingUsd + v.OrderNotionalUsd <= p.MaxLeverage * v.EquityUsd &&
                v.InstrumentGrossUsd + pendingUsd + v.OrderNotionalUsd <= p.MaxInstrumentUsd &&
                v.MarginUsedUsd + pendingMarginUsd + v.IncrementalMarginUsd <= Math.Min(p.MaxMarginUsd, v.EquityUsd) &&
                v.DailyLossUsd < p.MaxDailyLossUsd && pendingUsd + v.OrderNotionalUsd <= p.MaxPendingUsd &&
                pendingCount < p.MaxPendingCommands;
        }
        catch (OverflowException) { return false; }
    }
}
