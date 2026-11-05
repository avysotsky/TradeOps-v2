using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Normalization of actual addressed read replies on the already pinned session.</summary>
public static class DeribitAddressedQualificationParser
{
    public static OmsPrivateEventEnvelope Order(JsonElement result, DeribitPrivateInstrument metadata,
        AccountKey scope, string orderId, Guid epoch, DateTimeOffset receivedAtUtc)
    {
        var facts = DeribitWebSocketMessageParser.ParsePrivateFacts(result, metadata, false);
        if (facts.ExchangeOrderId != orderId || facts.FilledNativeAmount is null || facts.VenueTimestampUtc > receivedAtUtc)
            throw new ArgumentException("Addressed native order binding refused.");
        var envelope = new OmsPrivateEventEnvelope(scope, facts,
            new(Guid.NewGuid(), epoch, receivedAtUtc, OmsPrivateCaptureSource.RecoveryQuery, "private/get_order_state"));
        envelope.Validate(); return envelope;
    }

    public static IReadOnlyList<OmsPrivateEventEnvelope> Trades(JsonElement result, DeribitPrivateInstrument metadata,
        AccountKey scope, string orderId, Guid epoch, DateTimeOffset receivedAtUtc)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 1000)
            throw new ArgumentException("Bounded addressed trade array required.");
        return result.EnumerateArray().Select(item => {
            var facts = DeribitWebSocketMessageParser.ParsePrivateFacts(item, metadata, true);
            if (facts.ExchangeOrderId != orderId || facts.VenueTimestampUtc > receivedAtUtc || !facts.Fee.HasValue)
                throw new ArgumentException("Addressed execution binding/details refused.");
            var envelope = new OmsPrivateEventEnvelope(scope, facts,
                new(Guid.NewGuid(), epoch, receivedAtUtc, OmsPrivateCaptureSource.RecoveryQuery, "private/get_user_trades_by_order"));
            envelope.Validate(); return envelope;
        }).ToArray();
    }
}
