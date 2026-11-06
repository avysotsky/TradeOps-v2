using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradeOps.Connectors.Deribit;

/// <summary>Native comparison only. No order identity/command binding is inferred from this digest.</summary>
internal static class DeribitNativeOrderFingerprint
{
    internal static string Create(JsonElement order)
    {
        using var bytes = new MemoryStream(); using (var writer = new Utf8JsonWriter(bytes)) Write(writer, order);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new FormatException();
                writer.WriteStartObject(); foreach (var p in properties) { writer.WritePropertyName(p.Name); Write(writer, p.Value); } writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture)); break;
            default: value.WriteTo(writer); break;
        }
    }
}
