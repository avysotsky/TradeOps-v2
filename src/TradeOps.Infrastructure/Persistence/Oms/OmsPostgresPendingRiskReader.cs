using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Read committed immutable pending commands. During admission/dispatch the account head
/// is held by the caller. Uncommitted retirements may overcount; they can never undercount pending.</summary>
public sealed class OmsPostgresPendingRiskReader(NpgsqlDataSource source):IOmsPendingRiskReader
{
    public async Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(AccountKey scope,OmsCommandId except,CancellationToken ct=default)
    {
        await using var q=source.CreateCommand("""
SELECT CASE WHEN octet_length(c.request_json::text)<=16384 THEN c.request_json::text END,c.payload_sha256,r.command_sha256,r.schema_version FROM oms_risk_reservations_v1 r
JOIN oms_commands c USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
LEFT JOIN oms_risk_settlements_v1 s USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
WHERE r.tenant_id=@t AND r.venue_id=@v AND r.environment=@e AND r.account_id=@a AND r.subaccount_id=@s
AND r.command_id<>@except AND s.command_id IS NULL LIMIT 10001
""");
        q.Parameters.AddWithValue("t",scope.Tenant.Value);q.Parameters.AddWithValue("v",scope.Venue.Value);q.Parameters.AddWithValue("e",(short)scope.Environment);q.Parameters.AddWithValue("a",scope.Account.Value);q.Parameters.AddWithValue("s",scope.Subaccount?.Value??"");q.Parameters.AddWithValue("except",except.Value);
        var result=new List<OmsPendingNativeRiskV1>();await using var rows=await q.ExecuteReaderAsync(ct);
        while(await rows.ReadAsync(ct))
        {
            if(rows.IsDBNull(0)||result.Count>=10000)throw new InvalidOperationException("Pending risk capacity exceeded.");
            var c=OmsJournalRecoveryCommandCodec.Decode(rows.GetString(0),scope);var hash=OmsJournalCanonical.EncodeCommand(c).Sha256;
            if(!hash.SequenceEqual(rows.GetFieldValue<byte[]>(1))||!hash.SequenceEqual(rows.GetFieldValue<byte[]>(2))||c.Payload.Kind is not(OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace)||
                rows.GetInt32(3)!=(c.Payload.Kind==OmsJournalCommandKind.Replace?2:1)||
                c.Payload.QuantityUnit!=OmsQuantityUnit.QuoteAsset||c.Payload.OrderType!=TradeOps.Domain.Enums.OrderType.Limit||c.Payload.LimitPrice is not>0)
                throw new InvalidOperationException("Unsupported or corrupt pending risk units.");
            result.Add(new(c.Payload.Instrument,c.Payload.Quantity,c.Payload.LimitPrice.Value));
        }
        return result.AsReadOnly();
    }
}
