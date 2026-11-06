using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Activate-only runtime use of the same RBAC/audit path as operators. Never auto-clears.</summary>
public sealed class OmsPostgresAutomaticRiskCircuit(NpgsqlDataSource source)
{
    public async Task ActivateAsync(AccountKey scope,string reason,CancellationToken ct=default)
    {
        var key=OmsRiskControlScopeV1.ForAccount(scope);
        for(var retry=0;retry<3;retry++)
        {
            long revision=0;
            await using(var q=source.CreateCommand("""
SELECT revision,blocked FROM oms_risk_control_events_v1 WHERE level=3 AND kind=2 AND tenant_id=@t AND venue_id=@v
AND environment=@e AND account_id=@a AND subaccount_id=@s ORDER BY revision DESC LIMIT 1
"""))
            {
                q.Parameters.AddWithValue("t",key.Tenant);q.Parameters.AddWithValue("v",key.Venue);q.Parameters.AddWithValue("e",key.Environment);
                q.Parameters.AddWithValue("a",key.Account);q.Parameters.AddWithValue("s",key.Subaccount);
                await using var r=await q.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct)) { revision=r.GetInt64(0); if(r.GetBoolean(1)) return; }
            }
            try { await new OmsPostgresRiskControls(source).SetAsync(new(key,OmsRiskControlKind.CircuitBreaker,Guid.NewGuid(),revision,true,reason),ct); return; }
            catch(PostgresException e) when(e.SqlState=="40001"&&retry<2) { }
        }
        throw new InvalidOperationException("Risk circuit activation could not be retained.");
    }
}
