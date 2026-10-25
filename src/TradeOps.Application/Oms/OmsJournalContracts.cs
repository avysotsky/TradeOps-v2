using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>No transport or dispatcher lives in this package. Store-only immutable command data.</summary>
public enum OmsJournalCommandKind { Place = 1, Cancel = 2, Replace = 3 }
public enum OmsJournalWriteResult { Created = 1, Duplicate = 2, Collision = 3, StaleVersion = 4 }
public enum OmsJournalEventKind { Order = 1, Execution = 2 }
public enum OmsJournalInboxResult { Inserted = 1, Duplicate = 2, Collision = 3 }
public enum OmsJournalAttemptResult { Started = 1, AlreadyStartedOrResolved = 2, NotFound = 3 }
public enum OmsJournalDispatchOutcome { Acknowledged = 1, Rejected = 2, Ambiguous = 3 }
public enum OmsJournalFailPoint { BeforeCommit = 1 }

/// <summary>
/// Required fields form a complete versioned order command. Optional fields are explicit
/// nulls (not silently defaulted by a transport); no credentials/auth headers are supported.
/// </summary>
public sealed record OmsJournalCommandPayload(
    OmsJournalCommandKind Kind,
    string Instrument,
    OrderSide Side,
    OrderType OrderType,
    decimal Quantity,
    OmsQuantityUnit QuantityUnit,
    decimal? LimitPrice,
    decimal? TriggerPrice,
    string? TimeInForce,
    bool ReduceOnly,
    bool PostOnly,
    string Provenance,
    string OperatorOrStrategyId,
    string RiskDecisionReference,
    DateTimeOffset RequestedAtUtc)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Side) ||
            !Enum.IsDefined(OrderType) || !Enum.IsDefined(QuantityUnit))
            throw new ArgumentOutOfRangeException(nameof(Kind), "Unrecognized command or order enum.");
        Check(Instrument, 160, nameof(Instrument));
        Check(Provenance, 128, nameof(Provenance));
        Check(OperatorOrStrategyId, 128, nameof(OperatorOrStrategyId));
        Check(RiskDecisionReference, 128, nameof(RiskDecisionReference));
        if (TimeInForce is not null) Check(TimeInForce, 32, nameof(TimeInForce));
        if (Quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (LimitPrice is <= 0m || TriggerPrice is <= 0m)
            throw new ArgumentOutOfRangeException(nameof(LimitPrice), "Present prices must be positive.");
        if (RequestedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("RequestedAtUtc must have UTC offset zero.", nameof(RequestedAtUtc));
    }

    internal static void Check(string? text, int maximum, string param)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim())
            throw new ArgumentException($"Required opaque value must have length 1..{maximum} and no outer spaces.", param);
    }
}

public sealed record OmsJournalCommandRequest(
    AccountKey Scope,
    OmsLocalOrderId LocalOrderId,
    OrderIntentId IntentId,
    OmsCommandId CommandId,
    OrderVersion ExpectedAggregateVersion,
    OmsJournalCommandPayload Payload)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(Payload);
        OmsJournalCommandPayload.Check(Scope.Tenant.Value, 128, nameof(Scope.Tenant));
        OmsJournalCommandPayload.Check(Scope.Venue.Value, 64, nameof(Scope.Venue));
        OmsJournalCommandPayload.Check(Scope.Account.Value, 128, nameof(Scope.Account));
        if (Scope.Subaccount.HasValue)
            OmsJournalCommandPayload.Check(Scope.Subaccount.Value.Value, 128, nameof(Scope.Subaccount));
        if (LocalOrderId.Value == Guid.Empty || IntentId.Value == Guid.Empty || CommandId.Value == Guid.Empty)
            throw new ArgumentException("Zero GUID is not a valid journal identifier.");
        if (!Enum.IsDefined(Scope.Environment))
            throw new ArgumentOutOfRangeException(nameof(Scope.Environment));
        Payload.Validate();
    }
}

public sealed record OmsJournalInboxEvent(
    AccountKey Scope,
    string Namespace,
    string EventId,
    OmsJournalEventKind Kind,
    OmsLocalOrderId? LocalOrderId,
    string NativeInstrument,
    decimal? Quantity,
    decimal? Price,
    DateTimeOffset OccurredAtUtc)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        OmsJournalCommandPayload.Check(Namespace, 160, nameof(Namespace));
        OmsJournalCommandPayload.Check(EventId, 160, nameof(EventId));
        OmsJournalCommandPayload.Check(NativeInstrument, 160, nameof(NativeInstrument));
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (LocalOrderId.HasValue && LocalOrderId.Value.Value == Guid.Empty)
            throw new ArgumentException("Invalid aggregate ID", nameof(LocalOrderId));
        if (Quantity is <= 0m || Price is <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Quantity), "Present quantity and price must be positive.");
        if (Kind == OmsJournalEventKind.Execution && (!Quantity.HasValue || !Price.HasValue))
            throw new ArgumentException("Execution event requires quantity and price.");
        if (OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("OccurredAtUtc must be UTC.", nameof(OccurredAtUtc));
    }
}

/// <summary>Deterministic byte representation used to detect same-key/different-payload collisions.</summary>
public static class OmsJournalCanonical
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static (string Json, byte[] Sha256) EncodeCommand(OmsJournalCommandRequest request)
    {
        request.Validate();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = OmsContractSchema.Version,
            tenant = request.Scope.Tenant.Value,
            venue = request.Scope.Venue.Value,
            environment = (int)request.Scope.Environment,
            account = request.Scope.Account.Value,
            subaccount = request.Scope.Subaccount?.Value ?? "",
            localOrderId = request.LocalOrderId.Value,
            intentId = request.IntentId.Value,
            commandId = request.CommandId.Value,
            expectedVersion = request.ExpectedAggregateVersion.Value,
            command = request.Payload
        }, JsonOptions);
        return Digest(json);
    }

    public static (string Json, byte[] Sha256) EncodeInbox(OmsJournalInboxEvent item)
    {
        item.Validate();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = OmsContractSchema.Version,
            tenant = item.Scope.Tenant.Value,
            venue = item.Scope.Venue.Value,
            environment = (int)item.Scope.Environment,
            account = item.Scope.Account.Value,
            subaccount = item.Scope.Subaccount?.Value ?? "",
            eventNamespace = item.Namespace,
            eventId = item.EventId,
            kind = item.Kind.ToString(),
            aggregateId = item.LocalOrderId?.Value,
            instrument = item.NativeInstrument,
            quantity = item.Quantity,
            price = item.Price,
            occurredAtUtc = item.OccurredAtUtc
        }, JsonOptions);
        return Digest(json);
    }

    private static (string Json, byte[] Sha256) Digest(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 16384)
            throw new ArgumentException("OMS journal payload may not exceed 16 KiB.");
        return (json, SHA256.HashData(bytes));
    }
}
