using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

internal static class SignalAuditCursorCodec
{
    private const int Version = 1;

    public static string CreateFilterFingerprint(
        string? symbol,
        SignalOutcome? outcome,
        string? executionIssueCode,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toExclusive)
    {
        var canonical = JsonSerializer.Serialize(new FilterPayload(
            symbol,
            outcome?.ToString(),
            executionIssueCode,
            fromInclusive?.ToUniversalTime().ToString("O"),
            toExclusive?.ToUniversalTime().ToString("O")));

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Encode(
        SignalAuditCursorPosition position,
        string filterFingerprint)
    {
        var payload = new CursorPayload(
            Version,
            position.CreatedAt.ToUniversalTime(),
            position.Id,
            filterFingerprint);

        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return ToBase64Url(json);
    }

    public static bool TryDecode(
        string encoded,
        string expectedFilterFingerprint,
        out SignalAuditCursorPosition? position)
    {
        position = null;

        try
        {
            var bytes = FromBase64Url(encoded.Trim());
            var payload = JsonSerializer.Deserialize<CursorPayload>(bytes);

            if (payload is null
                || payload.Version != Version
                || payload.Id == Guid.Empty
                || payload.CreatedAt.Offset != TimeSpan.Zero
                || !string.Equals(
                    payload.FilterFingerprint,
                    expectedFilterFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            position = new SignalAuditCursorPosition(
                payload.CreatedAt,
                payload.Id);

            return true;
        }
        catch (Exception exception) when (
            exception is FormatException
            or JsonException
            or ArgumentException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = base64.Length % 4;
        if (padding == 2)
        {
            base64 += "==";
        }
        else if (padding == 3)
        {
            base64 += "=";
        }
        else if (padding == 1)
        {
            throw new FormatException("Invalid base64url length.");
        }

        return Convert.FromBase64String(base64);
    }

    private sealed record CursorPayload(
        int Version,
        DateTimeOffset CreatedAt,
        Guid Id,
        string FilterFingerprint);

    private sealed record FilterPayload(
        string? Symbol,
        string? Outcome,
        string? ExecutionIssueCode,
        string? FromInclusive,
        string? ToExclusive);
}
