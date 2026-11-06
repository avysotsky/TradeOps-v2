using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsRecoveryExternalOrderDecision { AcceptObservedExternalOrder = 1, WithdrawAcceptance }
public sealed record OmsRecoveryExternalOrderEvidence(DeribitRecoveredOrder Order, OmsPrivateVenueFacts LastReport,
    string? OpenOrderFingerprint);
public sealed record OmsRecoveryExternalOrderReview(int SchemaVersion, AccountKey Scope, Guid DecisionId, Guid SourceRunId,
    string Currency, string ExchangeOrderId, string EvidenceSha256, OmsRecoveryExternalOrderDecision Decision,
    string ActorRole, string Reason, DateTimeOffset RecordedAtUtc, long EvidenceVersion);
public sealed record OmsRecoveryExternalOrderReviewRequest(AccountKey Scope, Guid DecisionId, Guid SourceRunId,
    string Currency, string ExchangeOrderId, OmsRecoveryExternalOrderDecision Decision, string Reason, long ExpectedEvidenceVersion);

public interface IOmsRecoveryOperatorReviewStore
{
    Task<OmsRecoveryExternalOrderReview> ReviewAsync(OmsRecoveryExternalOrderReviewRequest request, CancellationToken ct = default);
}

/// <summary>Adopts an explicitly observed external native order into the baseline. It creates no
/// local command/receipt and cannot resolve a lost send, history gap, fill mismatch or capture failure.</summary>
public static class OmsRecoveryExternalOrderEvidencePolicy
{
    public static OmsRecoveryExternalOrderEvidence? Find(DeribitRecoveryRunDocumentV1 document, string currency, string orderId)
    {
        var order = document.Candidate?.Orders.Orders.SingleOrDefault(x => x.Currency == currency && x.ExchangeOrderId == orderId);
        if (order?.NativeState is null) return null;
        var facts = OmsRecoveryRetainedFacts.Collect(document.Ledger, document.Assessment.Ledger.Orders.AssessedAtUtc)
            .Concat((document.CurrencyObservations ?? []).SelectMany(x => x.Observations))
            .Concat(document.KnownOrderObservations?.Observations ?? []);
        var report = facts.Where(x => x.Facts.Currency == currency && x.Facts.ExchangeOrderId == orderId && x.Facts.Kind == OmsJournalEventKind.Order)
            .Select(x => x.Facts).OrderByDescending(x => x.VenueTimestampUtc).FirstOrDefault();
        if (report is null || report.OrderState != order.NativeState) return null;
        var open = document.InventoryObservations?.OpenOrders.SingleOrDefault(x => x.NativeInstrument == order.NativeInstrument && x.ExchangeOrderId == orderId);
        if (order.NativeState is "open" or "triggered" or "untriggered" && string.IsNullOrEmpty(open?.NativeFingerprint)) return null;
        return new(order with { PreviouslyObserved = false, HasCurrentEvidence = true }, report, open?.NativeFingerprint);
    }

    public static string Digest(OmsRecoveryExternalOrderEvidence evidence) => Convert.ToHexString(OmsRecoveryRunCodec.Encode(evidence).Sha256);
}
