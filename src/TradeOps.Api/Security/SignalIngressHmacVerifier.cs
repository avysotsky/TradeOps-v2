using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeOps.Api.Security;

internal static class SignalIngressHmacVerifier
{
    private const string SchemePrefix = "sha256=";

    public static bool Verify(
        string secret,
        long unixTimestamp,
        Guid requestId,
        string method,
        string path,
        ReadOnlySpan<byte> body,
        string suppliedSignature)
    {
        if (!TryDecodeSignature(suppliedSignature, out var suppliedHash))
        {
            return false;
        }

        var prefix = string.Join(
            "\n",
            unixTimestamp.ToString(CultureInfo.InvariantCulture),
            requestId.ToString("D"),
            method.ToUpperInvariant(),
            path,
            string.Empty);

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var payload = new byte[prefixBytes.Length + body.Length];

        prefixBytes.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefixBytes.Length));

        var key = Encoding.UTF8.GetBytes(secret);
        var expectedHash = HMACSHA256.HashData(key, payload);

        return CryptographicOperations.FixedTimeEquals(
            expectedHash,
            suppliedHash);
    }

    private static bool TryDecodeSignature(
        string suppliedSignature,
        out byte[] hash)
    {
        hash = Array.Empty<byte>();

        if (!suppliedSignature.StartsWith(
                SchemePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encodedHash = suppliedSignature[SchemePrefix.Length..];
        if (encodedHash.Length != 64)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(encodedHash);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
