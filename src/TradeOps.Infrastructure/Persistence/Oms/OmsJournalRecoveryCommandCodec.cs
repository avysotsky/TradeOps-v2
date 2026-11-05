using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

internal static class OmsJournalRecoveryCommandCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static OmsJournalCommandRequest Decode(string raw, AccountKey scope)
    {
        using var doc = JsonDocument.Parse(raw); var r = doc.RootElement;
        var command = new OmsJournalCommandRequest(scope, new(r.GetProperty("localOrderId").GetGuid()),
            new(r.GetProperty("intentId").GetGuid()), new(r.GetProperty("commandId").GetGuid()),
            new(r.GetProperty("expectedVersion").GetInt64()),
            r.GetProperty("command").Deserialize<OmsJournalCommandPayload>(Json)!);
        using var encoded = JsonDocument.Parse(OmsJournalCanonical.EncodeCommand(command).Json);
        if (!JsonElement.DeepEquals(r, encoded.RootElement)) throw new ArgumentException("Invalid journal command binding.");
        return command;
    }
}
