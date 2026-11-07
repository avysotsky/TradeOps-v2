using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsRecoveryRunState { Disconnected = 1, Recovering, Reconciling, Blocked, Ready }
public enum OmsRecoveryStopReason { CompletedWithIssues = 1, Cancelled, Failed, ConnectionLost, CaptureGap, AccountChanged }

/// <summary>A local recovery generation is not a physical sender lease or a venue sequence.</summary>
public sealed record OmsRecoveryRunStart(AccountKey Scope, Guid RunId, Guid Epoch,
    string NativeInstrument, string Currency, DateTimeOffset FromUtc, DateTimeOffset ThroughUtc)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        if (Scope.Venue.Value != "DERIBIT" || Scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) ||
            RunId == Guid.Empty || Epoch == Guid.Empty || string.IsNullOrWhiteSpace(NativeInstrument) ||
            NativeInstrument.Length > 160 || NativeInstrument != NativeInstrument.Trim() || NativeInstrument.Any(char.IsControl) ||
            Currency is not ("BTC" or "ETH") || FromUtc.Offset != TimeSpan.Zero || ThroughUtc.Offset != TimeSpan.Zero ||
            FromUtc <= DateTimeOffset.UnixEpoch || ThroughUtc < FromUtc || ThroughUtc - FromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Invalid bounded recovery run.");
    }
}

public sealed record OmsRecoveryRunToken(OmsRecoveryRunStart Start, long Generation);
public sealed record OmsRecoveryRunHead(OmsRecoveryRunToken Token, OmsRecoveryRunState State,
    DateTimeOffset UpdatedAtUtc)
{
    // Stored observations never grant runtime freshness or permission to increase risk.
    public bool CanIncreaseRisk => false;
}

/// <summary>Versioned private evidence stored in the account database, never public diagnostics.
/// A blocked run stores observations and issues without publishing a working account projection.</summary>
public sealed record DeribitRecoveryRunDocumentV1(int SchemaVersion, OmsRecoveryRunStart Start,
    DeribitRecoveryAccountLedgerAssessment Assessment, OmsRecoveryLedgerSnapshot Ledger,
    DeribitAccountSnapshotResult? AccountObservations,
    DeribitRecoveryQueryResult? QueryObservations = null, DeribitKnownOrderReadResult? KnownOrderObservations = null,
    DeribitRecoveryCandidateV1? Candidate = null, DeribitAccountInventoryResult? InventoryObservations = null,
    DeribitRecoveryUniverse? Universe = null, IReadOnlyList<DeribitCurrencyRecoveryResult>? CurrencyObservations = null,
    DeribitAccountInventoryResult? InitialInventoryObservations = null, DeribitRecoveryClockObservation? VenueClock = null,
    DeribitRecoveryCoherenceAssessment? Coherence = null)
{
    public void Validate()
    {
        Start.Validate();
        var a = Assessment.Ledger.Orders;
        if (SchemaVersion != 1 || a.Scope != Start.Scope || a.Epoch != Start.Epoch ||
            Coherence is not null && (Coherence.Policy != (Universe?.HistoryCoverage is null?DeribitRecoveryCoherencePolicy.Policy:DeribitRecoveryCoherencePolicy.CollateralPolicyV2) ||
                Coherence.Problems.Any(x => !Enum.IsDefined(x)) || Coherence.AssessedAtUtc.Offset != TimeSpan.Zero) ||
            a.NativeInstrument != Start.NativeInstrument || a.FromUtc != Start.FromUtc || a.ThroughUtc != Start.ThroughUtc ||
            a.SnapshotId != Ledger.Private.SnapshotId || Ledger.Private.Scope != Start.Scope ||
            !OmsRecoveryRunCodec.Encode(Assessment.Account.Evidence).Sha256.SequenceEqual(OmsRecoveryRunCodec.Encode(a).Sha256) || a.Blockers.Count == 0 ||
            a.Blockers.Any(x => !Enum.IsDefined(x)) || Assessment.AccountReady || Assessment.DurableProjectionPublished ||
            AccountObservations is not null && (AccountObservations.Scope != Start.Scope ||
                AccountObservations.Epoch != Start.Epoch || AccountObservations.Currency != Start.Currency) ||
            Candidate is not null && (Candidate.Scope != Start.Scope || Candidate.Epoch != Start.Epoch ||
                Candidate.Published || Candidate.AccountReady) ||
            InventoryObservations is not null && (InventoryObservations.Scope != Start.Scope || InventoryObservations.Epoch != Start.Epoch) ||
            InitialInventoryObservations is not null && (InitialInventoryObservations.Scope != Start.Scope || InitialInventoryObservations.Epoch != Start.Epoch) ||
            VenueClock is not null && (VenueClock.Scope != Start.Scope || VenueClock.Epoch != Start.Epoch) ||
            Universe is not null && (Universe.Scope != Start.Scope || Universe.Epoch != Start.Epoch || Universe.SnapshotId != a.SnapshotId) ||
            CurrencyObservations is not null && CurrencyObservations.Any(x => x.Scope != Start.Scope || x.Epoch != Start.Epoch ||
                x.Observations.Any(y => y.Scope != Start.Scope || y.Observation.StreamEpoch != Start.Epoch)))
            throw new ArgumentException("Recovery evidence does not match its blocked run.");
    }
}

public static class OmsRecoveryRunCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    public const int MaximumDocumentBytes = 32 * 1024 * 1024;

    private static JsonSerializerOptions CreateOptions()
    {
        // New recovery schema only. Do not change the durable journal/private-event encodings.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 64 };
        options.Converters.Add(new ValueConverter<TenantId, string>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<VenueId, string>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<TradingAccountId, string>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<TradingSubaccountId, string>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<OmsLocalOrderId, Guid>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<OrderIntentId, Guid>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<OmsCommandId, Guid>(x => x.Value, x => new(x)));
        options.Converters.Add(new ValueConverter<OrderVersion, long>(x => x.Value, x => new(x)));
        options.Converters.Add(new DecimalConverter());
        return options;
    }

    private sealed class ValueConverter<T, TValue>(Func<T, TValue> get, Func<TValue, T> create) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            create(JsonSerializer.Deserialize<TValue>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, get(value), options);
    }
    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        // Keep decimal scale when embedding old v1/v2 contracts: their existing canonical hashes depend on it.
        // Text in this new document survives jsonb numeric normalization without rewriting old encodings.
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    public static (string Json, byte[] Sha256) Encode<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new ArgumentException("Recovery document exceeds capacity.");
        return (json, SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static T Decode<T>(string json, byte[] digest)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes || digest.Length != 32)
            throw new InvalidOperationException("Invalid recovery document bounds.");
        var value = JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Missing recovery document.");
        // jsonb changes whitespace and member order; compare the original typed canonical representation.
        var canonical = Encode(value);
        if (!CryptographicOperations.FixedTimeEquals(canonical.Sha256, digest))
            throw new InvalidOperationException("Recovery document integrity failure.");
        return value;
    }
}

public interface IOmsRecoveryRunStore
{
    Task<OmsRecoveryRunToken> BeginAsync(OmsRecoveryRunStart start, CancellationToken ct = default);
    Task MarkReconcilingAsync(OmsRecoveryRunToken token, CancellationToken ct = default);
    Task CompleteBlockedAsync(OmsRecoveryRunToken token, DeribitRecoveryRunDocumentV1 document, CancellationToken ct = default);
    Task StopAsync(OmsRecoveryRunToken token, OmsRecoveryStopReason reason, CancellationToken ct = default);
    Task<OmsRecoveryRunHead?> ReadHeadAsync(AccountKey scope, CancellationToken ct = default);
    Task<DeribitRecoveryRunDocumentV1?> ReadResultAsync(AccountKey scope, Guid runId, CancellationToken ct = default);
}
