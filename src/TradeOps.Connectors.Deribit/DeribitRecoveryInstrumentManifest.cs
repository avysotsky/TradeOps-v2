using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Builds Application terms from actual immutable public catalog responses, not returned private order names.</summary>
public static class DeribitRecoveryInstrumentManifest
{
    public static IReadOnlyList<DeribitRecoveryInstrument> Create(OmsEnvironment environment,
        IReadOnlyList<DeribitInstrumentCatalogSnapshot> catalogs, DateTimeOffset now)
    {
        if (environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) || now.Offset != TimeSpan.Zero ||
            catalogs.Count is < 1 or > 32 || catalogs.Any(x => x.Environment != environment || x.ReceivedAtUtc > now ||
                now-x.ReceivedAtUtc > TimeSpan.FromMinutes(5)) ||
            catalogs.Select(x => (x.QueryCurrency, x.Kind)).Distinct().Count() != catalogs.Count)
            throw new ArgumentException("Invalid trusted native recovery catalogs.");
        var rows = catalogs.SelectMany(c => c.Instruments.Where(x => x.IsActive).Select(x => new DeribitRecoveryInstrument(
            x.Name, c.QueryCurrency, x.Kind, x.AmountUnit, x.AmountCurrency, x.BaseCurrency,
            x.Kind == "future" ? x.QuoteCurrency : x.BaseCurrency))).Take(4097).ToArray();
        if (rows.Length is < 1 or > 4096 || rows.Select(x => x.NativeInstrument).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new ArgumentException("Invalid bounded native recovery metadata.");
        return Array.AsReadOnly(rows.OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ToArray());
    }
}
