namespace TradeOps.Application.Oms;

/// <summary>Only proven native price revisions can explain a price change. A later observation
/// cannot regress to a previous price; no global relaxation of WS/history consistency.</summary>
public static class DeribitNativePriceHistoryV2
{
    // A later fill/status report is account evidence, not a zero-fill amendment ACK.
    // Associate it only after the exact price revision has already been proven.
    public static DeribitNativeCommandView? FollowUp(IReadOnlyList<DeribitNativeCommandView> views,DeribitFixExecutionReportV1 report)
    {
        report.Validate();
        var candidates=views.Where(x=>x.FixReport is not null&&x.Command.Payload.Kind is OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace&&
            x.FixReport.Session==report.Session&&x.Order.ExchangeOrderId==report.NativeOrderId&&x.Order.VenueTimestampUtc<=report.TransactTimeUtc)
            .OrderBy(x=>x.Command.ExpectedAggregateVersion.Value).ToArray();
        if(candidates.Length==0)return null;var latest=candidates[^1];var native=latest.FixReport!;
        return report.ReceivedAtUtc>=latest.ReceivedAtUtc&&report.OriginalClientClOrdId==native.OriginalClientClOrdId&&
            report.ServerAssignedClOrdId==native.ServerAssignedClOrdId&&report.Instrument==native.Instrument&&report.Side==native.Side&&
            report.HasUnambiguousUsdAmounts(1)&&report.OrderQty==native.OrderQty&&report.Price==native.Price&&report.OrderType==native.OrderType&&
            report.TimeInForce is null or "1"&&report.Label==native.Label&&report.ExecutionInstructions==native.ExecutionInstructions&&
            report.RejectReason==0&&report.OrderStatus is "0" or "1" or "2" or "4"&&report.CumQty>=native.CumQty?latest:null;
    }
    public static bool Matches(IReadOnlyList<DeribitNativeCommandView> views,DeribitNativeCommandView reference,OmsPrivateVenueFacts fact)
    {
        var revisions=views.Where(x=>x.Command.Scope==reference.Command.Scope&&x.Command.IntentId==reference.Command.IntentId&&
            x.Command.LocalOrderId==reference.Command.LocalOrderId&&x.Order.Currency==fact.Currency&&
            x.Order.ExchangeOrderId==fact.ExchangeOrderId&&x.Command.Payload.Kind is OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace)
            .OrderBy(x=>x.Command.ExpectedAggregateVersion.Value).ToArray();
        if(!revisions.Any(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Replace))return fact.PriceKind==reference.Order.PriceKind&&fact.Price==reference.Order.Price;
        if(revisions.Length==0||revisions[0].Command.Payload.Kind!=OmsJournalCommandKind.Place)return false;
        var selected=revisions.Where(x=>x.Order.VenueTimestampUtc<=fact.VenueTimestampUtc).LastOrDefault()??revisions[0];
        return fact.PriceKind==selected.Order.PriceKind&&fact.Price==selected.Order.Price;
    }
}
