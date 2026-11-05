using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Qualification admission only, never a production risk/READY or physical-send fence.</summary>
public static class DeribitBoundedQualificationPolicy
{
    public const string Prefix = "bounded-mainnet-oms-v1:";
    public const string Operator = "owner-launched-bounded-oms";
    public const string CurrentStage = "B4-bounded-oms-v1";
    public const int MaximumPlacements = 3;
    public const decimal MaximumNativeUsd = 1m;

    public static string Provenance(string stage)
    {
        if (stage.Length is < 3 or > 48 || !stage.StartsWith("B4-", StringComparison.Ordinal) ||
            stage.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Explicit bounded B4 stage ID required.");
        return Prefix + stage;
    }

    public static int ValidateAdmission(OmsRecoveryLedgerSnapshot snapshot, AccountKey scope, string stage)
    {
        var provenance = Provenance(stage);
        if (!snapshot.IsUsable || snapshot.Private.Scope != scope || scope.Venue.Value != "DERIBIT" ||
            scope.Environment != OmsEnvironment.Mainnet || snapshot.Commands.Count > 10000 ||
            snapshot.Private.SnapshotId == Guid.Empty || snapshot.Private.Observations.Count > 10000)
            throw new InvalidOperationException("Qualification snapshot refused.");
        var canonical = new Dictionary<(string, string), byte[]>();
        foreach (var observation in snapshot.Private.Observations)
        {
            observation.Envelope.Validate();
            if (observation.Envelope.Scope != scope || observation.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                observation.Envelope.Facts.VenueTimestampUtc > observation.Envelope.Observation.ReceivedAtUtc ||
                observation.Envelope.Observation.ReceivedAtUtc > snapshot.Private.ReadAtUtc.AddSeconds(5))
                throw new InvalidOperationException("Private qualification evidence refused.");
            var encoded = OmsPrivateEventCanonical.Encode(observation.Envelope); var key = (encoded.Namespace, encoded.EventId);
            if (canonical.TryGetValue(key, out var old) && !old.SequenceEqual(encoded.Sha256)) throw new InvalidOperationException("Private collision.");
            canonical[key] = encoded.Sha256;
        }
        if (canonical.Count != snapshot.Private.AcceptedFactCount) throw new InvalidOperationException("Partial private snapshot.");
        foreach (var command in snapshot.Commands) command.Validate(scope);
        DeribitCommandReceiptSet.Validate(scope, snapshot.Commands, snapshot.Receipts, snapshot.Private.ReadAtUtc);
        var commands = snapshot.Commands.Where(x => x.Request.Payload.Provenance.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        // Unknown sends anywhere in the account refuse qualification; known legacy ACKs are not
        // silently mapped or promoted to account readiness by this separate qualification path.
        if (snapshot.Commands.Any(x => (x.Phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous) &&
            !commands.Contains(x))) throw new InvalidOperationException("Unresolved non-qualification dispatch.");
        foreach (var command in commands)
        {
            var p = command.Request.Payload;
            if (p.OperatorOrStrategyId != Operator || p.Instrument != "ETH-PERPETUAL" ||
                p.Quantity != 1 || p.QuantityUnit != OmsQuantityUnit.QuoteAsset || !p.PostOnly || p.ReduceOnly ||
                p.Side != TradeOps.Domain.Enums.OrderSide.Buy || p.OrderType != TradeOps.Domain.Enums.OrderType.Limit ||
                p.Kind is not (OmsJournalCommandKind.Place or OmsJournalCommandKind.Cancel))
                throw new InvalidOperationException("Invalid qualification provenance/terms.");
            if (p.Kind != OmsJournalCommandKind.Place) continue;
            if (command.Phase == OmsRecoveryDispatchPhase.PersistedNotSent) continue; // Budget remains spent; no send occurred.
            var place = snapshot.Receipts.SingleOrDefault(x => x.Command.CommandId == command.Request.CommandId);
            var cancel = snapshot.Receipts.SingleOrDefault(x => x.Command.LocalOrderId == command.Request.LocalOrderId &&
                x.Command.Payload.Kind == OmsJournalCommandKind.Cancel);
            if (place is null || cancel is null) throw new InvalidOperationException("Previous qualification needs recovery.");
            if (cancel.Command.Payload.Provenance != p.Provenance || cancel.Command.Payload.OperatorOrStrategyId != Operator)
                throw new InvalidOperationException("Qualification cancel provenance mismatch.");
            DeribitCommandReceiptSet.ValidateCancelBinding(cancel, place);
            var facts = snapshot.Private.Observations.Where(x => x.Disposition is OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate)
                .Select(x => x.Envelope.Facts).Where(x => x.Currency == cancel.Order.Currency && x.ExchangeOrderId == cancel.Order.ExchangeOrderId).ToArray();
            var reports = facts.Where(x => x.Kind == OmsJournalEventKind.Order).OrderBy(x => x.VenueTimestampUtc).ToArray();
            if (place.Order.FilledNativeAmount != 0 || cancel.Order.FilledNativeAmount != 0 ||
                facts.Any(x => x.Kind == OmsJournalEventKind.Execution || x.FilledNativeAmount is > 0) ||
                reports.Any(x => x.NativeInstrument != cancel.Order.NativeInstrument || x.Side != cancel.Order.Side ||
                    x.NativeAmount != cancel.Order.NativeAmount || x.AmountUnit != cancel.Order.AmountUnit ||
                    x.AmountCurrency != cancel.Order.AmountCurrency || x.PriceKind != cancel.Order.PriceKind ||
                    x.Price != cancel.Order.Price || x.FilledNativeAmount != 0) ||
                reports.SkipWhile(x => x.OrderState != "cancelled").Any(x => x.OrderState != "cancelled") ||
                !facts.Any(x => x.Kind == OmsJournalEventKind.Order && x.OrderState == "cancelled" &&
                    x.FilledNativeAmount == 0 && x.VenueTimestampUtc >= cancel.Order.VenueTimestampUtc &&
                    x.NativeInstrument == "ETH-PERPETUAL" && x.NativeAmount == 1 && x.AmountUnit == OmsPrivateAmountUnit.Usd))
                throw new InvalidOperationException("Previous native qualification outcome unproven or filled.");
        }
        if (commands.Any(x => x.Request.Payload.Kind == OmsJournalCommandKind.Cancel &&
            !commands.Any(p => p.Request.Payload.Kind == OmsJournalCommandKind.Place && p.Request.LocalOrderId == x.Request.LocalOrderId)))
            throw new InvalidOperationException("Orphaned qualification cancel.");
        var spent = commands.Count(x => x.Request.Payload.Kind == OmsJournalCommandKind.Place && x.Request.Payload.Provenance == provenance);
        if (spent >= MaximumPlacements) throw new InvalidOperationException("Qualification stage budget exhausted.");
        return spent;
    }
}
