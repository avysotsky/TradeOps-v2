using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Api.Security;

internal static class ApiKeyVerifier
{
    public static bool Matches(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));

        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
