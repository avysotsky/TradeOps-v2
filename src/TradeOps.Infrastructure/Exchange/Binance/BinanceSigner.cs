using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Binance;

internal static class BinanceSigner
{
    public static string CreateHmacSha256Hex(
        string apiSecret,
        string payload)
    {
        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException("Binance API secret is not configured.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
