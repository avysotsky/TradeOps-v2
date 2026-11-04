using System.Security.Cryptography;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;

// One explicit public GET. No credentials, private endpoints, DB or worker registration.
if (args.Length != 3 || args[0] is not ("Mainnet" or "Testnet") || args[2] is not ("future" or "option"))
{
    Console.Error.WriteLine("Usage: TradeOps.DeribitInstrumentManifest <Mainnet|Testnet> <CURRENCY> <future|option>");
    return 2;
}
try
{
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    var snapshot = await new DeribitInstrumentCatalog(http).ReadAsync(
        args[0] == "Mainnet" ? OmsEnvironment.Mainnet : OmsEnvironment.Testnet, args[1], args[2]);
    var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.Instruments))).ToLowerInvariant();
    Console.WriteLine(JsonSerializer.Serialize(new { status = "PUBLIC_METADATA_PASS", environment = args[0],
        queryCurrency = snapshot.QueryCurrency, kind = snapshot.Kind, count = snapshot.Instruments.Count,
        active = snapshot.Instruments.Count(x => x.IsActive), usdAmount = snapshot.Instruments.Count(x => x.AmountUnit == OmsPrivateAmountUnit.Usd),
        baseCoinAmount = snapshot.Instruments.Count(x => x.AmountUnit == OmsPrivateAmountUnit.BaseCoin),
        snapshot.ReceivedAtUtc, metadataSha256 = digest, privateCalls = 0, mutatingCalls = 0, accountCoverageQualified = false }));
    return 0;
}
catch
{
    Console.Error.WriteLine("DERIBIT PUBLIC MANIFEST FAIL private_calls=0 mutating_calls=0");
    return 1;
}
