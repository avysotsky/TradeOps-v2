using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;

namespace TradeOps.Worker;

public sealed record DeribitRecoveryRuntimeBinding(DeribitPrivateCaptureBinding Binding,
    IReadOnlyList<DeribitRecoveryInstrument> Manifest);
public interface IDeribitRecoveryBindingSource
{
    Task<DeribitRecoveryRuntimeBinding> ReadAsync(CancellationToken ct = default);
}

/// <summary>Whole-account capture metadata comes from actual bounded public native catalogs.
/// Unknown/expired/unqualified products remain explicit recovery failures.</summary>
public sealed class DeribitRecoveryBindingSource(DeribitPrivateCaptureBinding pin, HttpClient http) : IDeribitRecoveryBindingSource
{
    public async Task<DeribitRecoveryRuntimeBinding> ReadAsync(CancellationToken ct = default)
    {
        var reader = new DeribitInstrumentCatalog(http); var catalogs = new List<DeribitInstrumentCatalogSnapshot>();
        foreach (var currency in new[] { "BTC", "ETH", "USDC", "USDT", "EURR" })
            foreach (var kind in new[] { "future", "option" })
                catalogs.Add(await reader.ReadAsync(pin.Scope.Environment, currency, kind, ct));
        var terms = DeribitRecoveryInstrumentManifest.Create(pin.Scope.Environment, catalogs, DateTimeOffset.UtcNow);
        var instruments = terms.Select(x => new DeribitPrivateInstrument(x.NativeInstrument, x.Kind, x.Currency, x.AmountUnit, x.AmountCurrency)).ToArray();
        DeribitInstrumentCatalog.ValidatePlan(pin.Scope.Environment, pin.Plan, catalogs);
        var plan = DeribitPrivateCapturePlan.ForAccountRecovery(pin.Plan.ExpectedAccountId, pin.Plan.IsSubaccount, instruments, pin.Plan.Limits);
        DeribitInstrumentCatalog.ValidatePlan(pin.Scope.Environment, plan, catalogs);
        return new(new(pin.Scope, pin.Options, plan), terms);
    }
}
