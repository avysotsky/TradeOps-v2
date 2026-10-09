using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

internal static class HyperliquidCloidFactory
{
    private const string Prefix = "TradeOps:Hyperliquid:";

    public static string Create(string clientOrderId)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException(
                "ClientOrderId is required.",
                nameof(clientOrderId));
        }

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(Prefix + clientOrderId.Trim()));

        return "0x" + Convert.ToHexString(hash.AsSpan(0, 16))
            .ToLowerInvariant();
    }

    public static bool IsValid(string value) =>
        value.Length == 34
        && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && value.AsSpan(2).ToString().All(Uri.IsHexDigit);
}
