using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsPrivateAmountUnit { Usd = 1, BaseCoin = 2 }
public enum OmsPrivatePriceKind { Unknown = 0, Numeric = 1, Market = 2 }
public enum OmsPrivateCaptureSource { WebSocket = 1, RecoveryQuery = 2 }
public enum OmsPrivateCaptureResult { Inserted = 1, Duplicate = 2, Collision = 3 }

/// <summary>
/// Native venue facts, not an Order/Fill projection. Null means unknown/not supplied;
/// no contracts conversion, timestamp fallback, local-order link or epoch belongs here.
/// </summary>
public sealed record OmsPrivateVenueFacts(
    OmsJournalEventKind Kind,
    string Currency,
    string NativeInstrument,
    string ExchangeOrderId,
    OrderSide Side,
    decimal NativeAmount,
    OmsPrivateAmountUnit AmountUnit,
    string AmountCurrency,
    DateTimeOffset VenueTimestampUtc,
    string? ExchangeTradeId = null,
    string? OrderState = null,
    decimal? FilledNativeAmount = null,
    decimal? LeavesNativeAmount = null,
    OmsPrivatePriceKind PriceKind = OmsPrivatePriceKind.Unknown,
    decimal? Price = null,
    decimal? AveragePrice = null,
    decimal? Fee = null,
    string? FeeCurrency = null,
    decimal? Contracts = null,
    string? ClientLabel = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Side) || !Enum.IsDefined(AmountUnit) ||
            !Enum.IsDefined(PriceKind)) throw new ArgumentException("Invalid private-event enum.");
        OmsPrivateEventCanonical.Token(Currency, 32, nameof(Currency));
        OmsPrivateEventCanonical.Token(AmountCurrency, 32, nameof(AmountCurrency));
        OmsPrivateEventCanonical.Opaque(NativeInstrument, 160, nameof(NativeInstrument));
        OmsPrivateEventCanonical.Opaque(ExchangeOrderId, 160, nameof(ExchangeOrderId));
        OmsPrivateEventCanonical.Utc(VenueTimestampUtc, nameof(VenueTimestampUtc));
        if (NativeAmount <= 0 || Contracts is <= 0 || Price is < 0 || AveragePrice is < 0 ||
            FilledNativeAmount is < 0 || LeavesNativeAmount is < 0)
            throw new ArgumentOutOfRangeException(nameof(NativeAmount));
        if (AmountUnit == OmsPrivateAmountUnit.Usd && AmountCurrency != "USD")
            throw new ArgumentException("USD-native amounts require USD currency.");
        if ((PriceKind == OmsPrivatePriceKind.Numeric) != Price.HasValue)
            throw new ArgumentException("Numeric price kind requires a price; other kinds cannot contain one.");
        if (ClientLabel is not null) OmsPrivateEventCanonical.Opaque(ClientLabel, 160, nameof(ClientLabel));
        if (Fee.HasValue != (FeeCurrency is not null))
            throw new ArgumentException("Fee and fee currency must be supplied together.");
        if (FeeCurrency is not null) OmsPrivateEventCanonical.Token(FeeCurrency, 32, nameof(FeeCurrency));
        if (Kind == OmsJournalEventKind.Execution)
        {
            OmsPrivateEventCanonical.Opaque(ExchangeTradeId, 160, nameof(ExchangeTradeId));
            if (OrderState is not null || FilledNativeAmount.HasValue || LeavesNativeAmount.HasValue ||
                AveragePrice.HasValue || PriceKind != OmsPrivatePriceKind.Numeric)
                throw new ArgumentException("Execution facts require an execution price, not an order snapshot.");
        }
        else
        {
            if (ExchangeTradeId is not null || Fee.HasValue)
                throw new ArgumentException("Order reports cannot invent execution IDs or execution fees.");
            if (OrderState is not ("open" or "filled" or "rejected" or "cancelled" or "untriggered" or "triggered"))
                throw new ArgumentException("An explicit supported native order state is required.");
            if (FilledNativeAmount > NativeAmount || LeavesNativeAmount > NativeAmount ||
                (FilledNativeAmount.HasValue && LeavesNativeAmount.HasValue &&
                 FilledNativeAmount + LeavesNativeAmount > NativeAmount))
                throw new ArgumentException("Inconsistent quantities must be reconciled, not clamped.");
        }
    }
}

/// <summary>CaptureId is stable across a retry of this observation, epoch is not an execution ID.</summary>
public sealed record OmsPrivateObservation(Guid CaptureId, Guid StreamEpoch,
    DateTimeOffset ReceivedAtUtc, OmsPrivateCaptureSource Source, string Channel)
{
    public void Validate()
    {
        if (CaptureId == Guid.Empty || StreamEpoch == Guid.Empty || !Enum.IsDefined(Source))
            throw new ArgumentException("A capture ID, stream epoch and source are required.");
        OmsPrivateEventCanonical.Utc(ReceivedAtUtc, nameof(ReceivedAtUtc));
        OmsPrivateEventCanonical.Opaque(Channel, 160, nameof(Channel));
    }
}

public sealed record OmsPrivateEventEnvelope(AccountKey Scope, OmsPrivateVenueFacts Facts,
    OmsPrivateObservation Observation)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(Facts);
        ArgumentNullException.ThrowIfNull(Observation);
        OmsPrivateEventCanonical.Opaque(Scope.Tenant.Value, 128, nameof(Scope.Tenant));
        OmsPrivateEventCanonical.Opaque(Scope.Account.Value, 128, nameof(Scope.Account));
        if (Scope.Subaccount.HasValue)
            OmsPrivateEventCanonical.Opaque(Scope.Subaccount.Value.Value, 128, nameof(Scope.Subaccount));
        if (Scope.Venue.Value != "DERIBIT" || !Enum.IsDefined(Scope.Environment))
            throw new ArgumentException("This private capture contract supports Deribit only.");
        Facts.Validate();
        Observation.Validate();
    }
}

/// <summary>
/// Store-only application port. Return means committed capture, not projection/fill/READY.
/// Caller must supply a trusted full account scope. No execution or legacy writes are exposed.
/// </summary>
public interface IOmsPrivateEventCapture
{
    Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}

public sealed record OmsPrivateEncodedEvent(string Namespace, string EventId, string Json, byte[] Sha256);

public static class OmsPrivateEventCanonical
{
    public const int EventSchemaVersion = 2;
    public const int ObservationSchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { Converters = { new CanonicalDecimalConverter() } };

    public static OmsPrivateEncodedEvent Encode(OmsPrivateEventEnvelope envelope)
    {
        envelope.Validate();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = EventSchemaVersion,
            tenant = envelope.Scope.Tenant.Value,
            venue = envelope.Scope.Venue.Value,
            environment = (int)envelope.Scope.Environment,
            account = envelope.Scope.Account.Value,
            subaccount = envelope.Scope.Subaccount?.Value ?? "",
            facts = envelope.Facts
        }, Options);
        var digest = Digest(json);
        var execution = envelope.Facts.Kind == OmsJournalEventKind.Execution;
        // Per-currency trade identity + the participant's own order preserves distinct legs.
        // Side/instrument/quantity stay in the payload so changing them is a collision.
        var id = execution ? Digest(JsonSerializer.Serialize(new[]
            { envelope.Facts.ExchangeTradeId!, envelope.Facts.ExchangeOrderId }, Options)) : digest;
        var ns = execution ? "Deribit:execution-leg:v2:" : "Deribit:order-report:v2:";
        // Order reports are content-addressed observations, NOT venue revisions or fills.
        return new(ns + envelope.Facts.Currency, Convert.ToHexStringLower(id), json, digest);
    }

    public static (string Json, byte[] Sha256) EncodeObservation(OmsPrivateEventEnvelope envelope,
        OmsPrivateEncodedEvent encoded)
    {
        envelope.Validate();
        using var document = JsonDocument.Parse(encoded.Json);
        var json = JsonSerializer.Serialize(new
        {
            captureSchemaVersion = ObservationSchemaVersion,
            eventNamespace = encoded.Namespace,
            eventId = encoded.EventId,
            // Retain the entire incoming versioned facts even when quarantined as a collision.
            venueEvent = document.RootElement,
            observation = envelope.Observation
        }, Options);
        return (json, Digest(json));
    }

    private static byte[] Digest(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 32768) throw new ArgumentException("Private capture payload exceeds 32 KiB.");
        return SHA256.HashData(bytes);
    }

    internal static void Opaque(string? value, int maximum, string parameter)
    {
        OmsJournalCommandPayload.Check(value, maximum, parameter);
        if (value!.Any(char.IsControl)) throw new ArgumentException("Control characters are not identifiers.", parameter);
    }

    internal static void Token(string value, int maximum, string parameter)
    {
        Opaque(value, maximum, parameter);
        if (value.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')))
            throw new ArgumentException("Currency must be an explicit uppercase venue token.", parameter);
    }

    internal static void Utc(DateTimeOffset time, string parameter)
    {
        if (time.Offset != TimeSpan.Zero || time <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("An explicit UTC venue/receipt timestamp is required.", parameter);
    }

    private sealed class CanonicalDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }
}
