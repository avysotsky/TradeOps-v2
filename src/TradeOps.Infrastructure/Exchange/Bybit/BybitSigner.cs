using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Bybit;

internal static class BybitSigner
{
    public static string CreateHmacSha256Hex(
        string apiSecret,
        long timestamp,
        string apiKey,
        int recvWindowMilliseconds,
        string payload)
    {
        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException("Bybit API secret is not configured.");
        }

        var plainText = $"{timestamp}{apiKey}{recvWindowMilliseconds}{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(plainText));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
