using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Explicit active ETH inverse futures policy. Construct before private I/O, never from returned positions.</summary>
public static class DeribitEthFutureAccountManifest
{
    public static IReadOnlyList<DeribitPrivateInstrument> Create(DeribitInstrumentCatalogSnapshot catalog,
        OmsEnvironment environment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) || catalog.Environment != environment ||
            catalog.QueryCurrency != "ETH" || catalog.Kind != "future" || now.Offset != TimeSpan.Zero ||
            catalog.ReceivedAtUtc.Offset != TimeSpan.Zero || catalog.ReceivedAtUtc <= DateTimeOffset.UnixEpoch ||
            now < catalog.ReceivedAtUtc || now - catalog.ReceivedAtUtc > TimeSpan.FromSeconds(30) ||
            catalog.Instruments.Count is < 1 or > 4096 ||
            catalog.Instruments.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Instruments.Count)
            throw new InvalidOperationException("ETH future account manifest refused.");
        var active = catalog.Instruments.Where(x => x.IsActive).ToArray();
        if (active.Length is < 1 or > 256 || !active.Any(x => x.Name == "ETH-PERPETUAL") ||
            active.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 160 || x.Name != x.Name.Trim() || x.Name.Any(char.IsControl) ||
                x.Kind != "future" || x.InstrumentType != "reversed" || x.BaseCurrency != "ETH" || x.QuoteCurrency != "USD" ||
                x.SettlementCurrency != "ETH" || x.AmountUnit != OmsPrivateAmountUnit.Usd || x.AmountCurrency != "USD"))
            throw new InvalidOperationException("ETH future account manifest refused.");
        return Array.AsReadOnly(active.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x =>
            new DeribitPrivateInstrument(x.Name, "future", "ETH", OmsPrivateAmountUnit.Usd, "USD")).ToArray());
    }
}
