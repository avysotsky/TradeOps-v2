using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Server-owned terms from native public metadata. Never reconstruct these from the instrument name.</summary>
public sealed record DeribitRecoveryInstrument(string NativeInstrument, string Currency, string Kind,
    OmsPrivateAmountUnit AmountUnit, string AmountCurrency, string? BaseCurrency = null, string? PositionSizeCurrency = null);
public enum DeribitRecoveryUniverseProblem { InvalidInventory = 1, StaleInventory, InvalidJournal,
    InvalidManifest, UnmappedInstrument, UnsupportedCurrency, Capacity, CurrencyHistoryUnavailable, PositionUnitsUnavailable, AccountObservationsChanged, VenueClockUnavailable }
public sealed record DeribitRecoveryUniverseIssue(DeribitRecoveryUniverseProblem Problem, string? NativeInstrument = null, string? Currency = null);
public sealed record DeribitRecoveryUniverse(AccountKey Scope, Guid Epoch, Guid SnapshotId,
    IReadOnlyList<string> RequiredInstruments, IReadOnlyList<string> RequiredCurrencies,
    IReadOnlyList<DeribitRecoveryUniverseIssue> Issues, IReadOnlyList<DeribitRecoveryInstrument>? InstrumentTerms = null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeribitCollateralHistoryCoverageV2? HistoryCoverage=null)
{
    public bool UniverseResolved => Issues.Count == 0;
    public bool AccountReady => false;
}

public static class DeribitRecoveryUniverseBuilder
{
    public static DeribitRecoveryUniverse Build(AccountKey scope, Guid epoch, OmsRecoveryLedgerSnapshot ledger,
        DeribitAccountInventoryResult inventory, IReadOnlyList<DeribitRecoveryInstrument> trustedManifest, DateTimeOffset now,
        DeribitHistoryCoverageMode coverageMode=DeribitHistoryCoverageMode.InventoryCurrenciesV1)
    {
        var issues = new HashSet<DeribitRecoveryUniverseIssue>();
        var symbols = new HashSet<string>(StringComparer.Ordinal); var currencies = new HashSet<string>(StringComparer.Ordinal);
        DeribitCollateralHistoryCoverageV2? history=null;
        if(!Enum.IsDefined(coverageMode))throw new ArgumentException("Unsupported native history coverage policy.");
        if (scope.Venue.Value != "DERIBIT" || scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) || epoch == Guid.Empty || now.Offset != TimeSpan.Zero ||
            inventory.Scope != scope || inventory.Epoch != epoch || !inventory.QueriesCompleted ||
            inventory.StartedAtUtc.Offset != TimeSpan.Zero || inventory.CompletedAtUtc.Offset != TimeSpan.Zero ||
            inventory.CompletedAtUtc < inventory.StartedAtUtc || inventory.CompletedAtUtc > now ||
            inventory.MaximumObservationSpan < TimeSpan.FromSeconds(1) || inventory.MaximumObservationSpan > TimeSpan.FromSeconds(60) ||
            inventory.Balances.Count is < 1 or > 32 || inventory.Positions.Count > 4096 || inventory.OpenOrders.Count > 4096)
        { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidInventory)); return Finish(); }
        if (!inventory.IsFreshAt(now)) { issues.Add(new(DeribitRecoveryUniverseProblem.StaleInventory)); return Finish(); }
        if (!ledger.IsUsable || ledger.Private.Scope != scope || ledger.Private.SnapshotId == Guid.Empty ||
            ledger.Private.ReadAtUtc.Offset != TimeSpan.Zero || ledger.Private.ReadAtUtc > now ||
            ledger.Commands.Count > 10000 || ledger.Private.Observations.Count > 10000)
        { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidJournal)); return Finish(); }
        if (trustedManifest.Count > 4096 || trustedManifest.Any(x => x is null || !Opaque(x.NativeInstrument, 160) ||
            !Token(x.Currency) || !Token(x.AmountCurrency) || x.Kind is not ("future" or "option") || !Enum.IsDefined(x.AmountUnit) ||
            x.AmountUnit == OmsPrivateAmountUnit.Usd && x.AmountCurrency != "USD" || x.BaseCurrency is not null && !Token(x.BaseCurrency) ||
            x.PositionSizeCurrency is not null && !Token(x.PositionSizeCurrency)) ||
            trustedManifest.Select(x => x.NativeInstrument).Distinct(StringComparer.Ordinal).Count() != trustedManifest.Count)
        { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidManifest)); return Finish(); }
        var metadata = trustedManifest.ToDictionary(x => x.NativeInstrument, StringComparer.Ordinal);
        IReadOnlyList<OmsPrivateEventEnvelope> retained;
        try
        {
            foreach (var command in ledger.Commands) command.Validate(scope);
            if (ledger.Commands.Select(x => x.Request.CommandId).Distinct().Count() != ledger.Commands.Count) throw new ArgumentException();
            _ = DeribitNativeCommandEvidenceV1.Read(ledger);
            retained = OmsRecoveryRetainedFacts.Collect(ledger, now);
            foreach (var row in ledger.Private.Observations)
            {
                row.Envelope.Validate();
                if (row.Envelope.Scope != scope || row.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                    row.Envelope.Observation.ReceivedAtUtc > ledger.Private.ReadAtUtc || row.Envelope.Facts.VenueTimestampUtc > ledger.Private.ReadAtUtc)
                    throw new ArgumentException();
            }
        }
        catch (ArgumentException) { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidJournal)); return Finish(); }
        if (inventory.Balances.Any(x => !Token(x.Currency)) ||
            inventory.Balances.Any(x => x.Window is null || x.Window.RequestedAtUtc.Offset != TimeSpan.Zero ||
                x.Window.ReceivedAtUtc.Offset != TimeSpan.Zero || x.Window.RequestedAtUtc < inventory.StartedAtUtc ||
                x.Window.ReceivedAtUtc < x.Window.RequestedAtUtc || x.Window.ReceivedAtUtc > inventory.CompletedAtUtc) ||
            inventory.Balances.Select(x => x.Currency).Distinct(StringComparer.Ordinal).Count() != inventory.Balances.Count ||
            inventory.Positions.Any(x => !Opaque(x.NativeInstrument, 160) || !Opaque(x.Kind, 32) || !Sign(x.Direction, x.NativeSize) ||
                x.NativeBaseSize.HasValue && !Sign(x.Direction, x.NativeBaseSize.Value)) ||
            inventory.Positions.Select(x => x.NativeInstrument).Distinct(StringComparer.Ordinal).Count() != inventory.Positions.Count ||
            inventory.OpenOrders.Any(x => !Opaque(x.NativeInstrument, 160) || !Opaque(x.ExchangeOrderId, 160) ||
                x.NativeState is not ("open" or "untriggered" or "triggered") || !Opaque(x.NativeOrderType, 32)))
        { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidInventory)); return Finish(); }
        if(coverageMode==DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2)
        {
            // Always enumerate all derivative settlement currencies, including zero holdings.
            // Keep every collateral row in inventory and in the explicit versioned coverage record.
            history=DeribitCollateralHistoryCoverageV2.Create(inventory.Balances);
            foreach(var currency in DeribitCollateralHistoryCoverageV2.Settlements)currencies.Add(currency);
            foreach(var balance in inventory.Balances)
                if(!DeribitCollateralHistoryCoverageV2.Known(balance.Currency))issues.Add(new(DeribitRecoveryUniverseProblem.UnsupportedCurrency,Currency:balance.Currency));
        }
        else foreach (var balance in inventory.Balances) currencies.Add(balance.Currency); // v1 remains byte/behavior compatible.
        foreach (var position in inventory.Positions)
        {
            symbols.Add(position.NativeInstrument);
            if (metadata.TryGetValue(position.NativeInstrument, out var m) && m.Kind != position.Kind)
                issues.Add(new(DeribitRecoveryUniverseProblem.InvalidManifest, position.NativeInstrument));
        }
        foreach (var order in inventory.OpenOrders) symbols.Add(order.NativeInstrument);
        foreach (var command in ledger.Commands) symbols.Add(command.Request.Payload.Instrument); // Includes unsent and unresolved commands.
        foreach (var row in retained)
        { symbols.Add(row.Facts.NativeInstrument); currencies.Add(row.Facts.Currency); }
        foreach (var symbol in symbols)
            if (metadata.TryGetValue(symbol, out var m)) currencies.Add(m.Currency);
            else issues.Add(new(DeribitRecoveryUniverseProblem.UnmappedInstrument, symbol));
        foreach (var currency in currencies)
            if (currency is not ("BTC" or "ETH" or "USDC" or "USDT" or "EURR"))
                issues.Add(new(DeribitRecoveryUniverseProblem.UnsupportedCurrency, Currency: currency));
        if (symbols.Count > 4096) issues.Add(new(DeribitRecoveryUniverseProblem.Capacity));
        return Finish();

        DeribitRecoveryUniverse Finish() => new(scope, epoch, ledger.Private.SnapshotId,
            symbols.Order(StringComparer.Ordinal).ToArray(), currencies.Order(StringComparer.Ordinal).ToArray(),
            issues.OrderBy(x => x.Problem).ThenBy(x => x.NativeInstrument, StringComparer.Ordinal).ThenBy(x => x.Currency, StringComparer.Ordinal).ToArray(),
            trustedManifest.Where(x => x is not null && symbols.Contains(x.NativeInstrument)).OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ToArray(),history);
    }
    private static bool Opaque(string? value, int max) => value is { Length: > 0 } && value.Length <= max && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Token(string? value) => Opaque(value, 32) && value!.All(x => char.IsAsciiLetterUpper(x) || char.IsAsciiDigit(x) || x == '_');
    private static bool Sign(string direction, decimal size) => direction switch { "buy" => size > 0, "sell" => size < 0, "zero" => size == 0, _ => false };
}
