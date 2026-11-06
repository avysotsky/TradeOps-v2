namespace TradeOps.Application.Oms;

/// <summary>Retire a reservation only after a current B4 baseline proves a terminal native order
/// and the complete deduplicated fills. A direct placement receipt establishes identity, not finality.</summary>
public static class OmsRiskSettlementV1
{
    public static bool CanRetire(OmsRecoveredAccountStateV1 baseline, DeribitJsonRpcCommandReceipt placement)
    {
        try
        {
            placement.Validate(); var command = placement.Command;
            if (command.Scope != baseline.Scope || command.Payload.Kind != OmsJournalCommandKind.Place ||
                placement.ReceivedAtUtc > baseline.NativeThroughUtc) return false;
            var matches = baseline.Orders.Where(x => x.LocalOrderId == command.LocalOrderId &&
                x.State.Currency == placement.Order.Currency && x.State.ExchangeOrderId == placement.Order.ExchangeOrderId).ToArray();
            if (matches.Length != 1) return false;
            var order = matches[0]; var s = order.State; var report = order.LastReport;
            report.Validate();
            if (s.NativeState is not ("filled" or "cancelled" or "rejected") || report.OrderState != s.NativeState ||
                report.Currency != s.Currency || report.ExchangeOrderId != s.ExchangeOrderId || report.NativeInstrument != s.NativeInstrument ||
                report.Side != s.Side || report.NativeAmount != s.NativeAmount || report.AmountUnit != s.AmountUnit || report.AmountCurrency != s.AmountCurrency ||
                s.NativeInstrument != command.Payload.Instrument || s.Side != command.Payload.Side || s.NativeAmount != command.Payload.Quantity ||
                s.AmountUnit != placement.Order.AmountUnit || s.AmountCurrency != placement.Order.AmountCurrency ||
                s.KnownFilledNativeAmount is not { } filled || filled < 0 || filled > s.NativeAmount ||
                report.FilledNativeAmount != filled || report.VenueTimestampUtc > baseline.NativeThroughUtc ||
                report.VenueTimestampUtc < placement.Order.VenueTimestampUtc ||
                s.NativeState == "filled" && filled != s.NativeAmount || s.NativeState == "rejected" && filled != 0) return false;
            var fills = baseline.Executions.Where(x => x.Currency == s.Currency && x.ExchangeOrderId == s.ExchangeOrderId).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal); decimal sum = 0;
            foreach (var f in fills)
            {
                f.Validate();
                if (f.Kind != OmsJournalEventKind.Execution || f.NativeInstrument != s.NativeInstrument || f.Side != s.Side ||
                    f.AmountUnit != s.AmountUnit || f.AmountCurrency != s.AmountCurrency || f.ExchangeTradeId is null || !ids.Add(f.ExchangeTradeId) ||
                    f.VenueTimestampUtc > baseline.NativeThroughUtc) return false;
                sum += f.NativeAmount;
            }
            return sum == filled;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException) { return false; }
    }
}
