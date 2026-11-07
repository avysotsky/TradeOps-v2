using System.Globalization;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed class OmsPostgresRiskPolicyReader(NpgsqlDataSource source) : IOmsRiskPolicyReader
{
    public async Task<OmsRiskPolicyDocumentV1?> ReadAsync(AccountKey scope, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("""
SELECT revision,schema_version,CASE WHEN octet_length(policy_json::text)<=65536 THEN policy_json::text END,policy_sha256
FROM oms_account_risk_policies_v1 WHERE tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s
ORDER BY revision DESC LIMIT 1
""");
        q.Parameters.AddWithValue("t",scope.Tenant.Value); q.Parameters.AddWithValue("v",scope.Venue.Value); q.Parameters.AddWithValue("e",(short)scope.Environment);
        q.Parameters.AddWithValue("a",scope.Account.Value); q.Parameters.AddWithValue("s",scope.Subaccount?.Value ?? "");
        await using var r = await q.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        var schema=r.GetInt32(1);
        if (schema is not(1 or 2) || r.IsDBNull(2)) throw new InvalidOperationException("Unsupported account risk policy.");
        var p = OmsRecoveryRunCodec.Decode<OmsRiskPolicyDocumentV1>(r.GetString(2), r.GetFieldValue<byte[]>(3));
        if (p.SchemaVersion != schema ||p.Limits.SchemaVersion!=schema||p.SchemaVersion==1&&p.CrossPortfolio is not null||p.SchemaVersion==2&&!DeribitCrossPortfolioRiskV2.PolicyValid(p)||
            p.Limits.Scope != scope || p.Limits.Revision != r.GetInt64(0).ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Account policy binding mismatch.");
        return p;
    }
}
