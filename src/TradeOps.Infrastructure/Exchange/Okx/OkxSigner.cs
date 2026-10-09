using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Infrastructure.Exchange.Okx;

internal static class OkxSigner
{
    public static string Sign(
        string timestamp,
        string method,
        string requestPath,
        string body,
        string secret)
    {
        var prehash =
            timestamp
            + method.ToUpperInvariant()
            + requestPath
            + body;

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(secret));

        return Convert.ToBase64String(
            hmac.ComputeHash(
                Encoding.UTF8.GetBytes(prehash)));
    }
}
