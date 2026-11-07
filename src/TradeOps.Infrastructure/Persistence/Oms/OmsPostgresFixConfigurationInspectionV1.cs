using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed record OmsFixConfigurationFindingV1(string Code,bool Passed,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Reason=null);

/// <summary>Diagnostic only. All connections must enforce transaction read-only at startup.
/// No capture/gateway/session owner, migration, provisioning, venue client or physical permit.</summary>
public static class OmsPostgresFixConfigurationInspectionV1
{
    public static async Task<IReadOnlyList<OmsFixConfigurationFindingV1>> InspectAsync(
        IReadOnlyDictionary<string,NpgsqlDataSource> sources,AccountKey scope,OmsFixSessionKeyV1 session,string root,CancellationToken ct=default)
    {
        var findings=new List<OmsFixConfigurationFindingV1>();
        var identities=new List<(string User,string Database,string Server)>();
        var readOnly=true;
        foreach(var name in new[]{"Capture","Recovery","Execution","Fix"})
        {
            try
            {
                var source=sources[name];var settings=new NpgsqlConnectionStringBuilder(source.ConnectionString);
                if(settings.Options!="-c default_transaction_read_only=on"||settings.Multiplexing||settings.Pooling)
                    throw new InvalidOperationException();
                await using var q=source.CreateCommand("SELECT current_user::text,current_database()::text,coalesce(inet_server_addr()::text,'local')||':'||coalesce(inet_server_port()::text,''),current_setting('transaction_read_only'),session_user::text");
                await using var row=await q.ExecuteReaderAsync(ct);if(!await row.ReadAsync(ct)||row.GetString(3)!="on"||row.GetString(0)!=row.GetString(4))throw new InvalidOperationException();
                identities.Add((row.GetString(0),row.GetString(1),row.GetString(2)));
            }
            catch{readOnly=false;}
        }
        findings.Add(new("ReadOnlyConnections",readOnly));
        var same=identities.Count==4&&identities.Select(x=>(x.Database,x.Server)).Distinct().Count()==1&&identities.Select(x=>x.User).Distinct(StringComparer.Ordinal).Count()==4;
        findings.Add(new("FourDistinctDatabaseIdentities",same));
        // Refuse even catalog work through a connection that did not prove read-only.
        if(!readOnly||!same)return findings;
        await Check("CaptureRole",()=>OmsPostgresPrivateCaptureOwner.ValidateRoleAsync(sources["Capture"],ct));
        await Check("RecoveryRole",()=>OmsPostgresRecoveryRuntimeGuard.ValidateAsync(sources["Recovery"],ct));
        await Check("ExecutionRole",()=>OmsPostgresExecutionRuntimeGuard.ValidateAsync(sources["Execution"],ct,scope));
        await Check("ProtocolRole",()=>OmsPostgresFixRuntimeGuardV1.ValidateAsync(sources["Fix"],ct));
        await Check("AmendmentSchemaV2",async()=>
        {
            await using var q=sources["Execution"].CreateCommand("""
SELECT to_regclass('public.oms_fix_amend_messages_v2') IS NOT NULL
AND (SELECT count(*)=2 FROM pg_constraint WHERE conname IN
 ('oms_deribit_fix_command_bindings_v1_schema_version_check','oms_risk_reservations_v1_schema_version_check')
 AND connamespace='public'::regnamespace AND contype='c' AND pg_get_constraintdef(oid) LIKE '%ARRAY[1, 2]%')
""");
            if(await q.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException();
        });
        await Check("ProvisionedFixSession",async()=>
        {
            session.Validate();if(session.Scope!=scope)throw new InvalidOperationException();
            // Async catalog/data inspection, not construction of a mutable protocol store.
            await using var q=sources["Fix"].CreateCommand("""
SELECT schema_version,key_sha256,next_sender,next_target FROM oms_fix_sessions_v1
WHERE tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s
AND sender_comp_id=@sender AND target_comp_id=@target
""");
            q.Parameters.AddWithValue("t",scope.Tenant.Value);q.Parameters.AddWithValue("v",scope.Venue.Value);q.Parameters.AddWithValue("e",(short)scope.Environment);
            q.Parameters.AddWithValue("a",scope.Account.Value);q.Parameters.AddWithValue("s",scope.Subaccount?.Value??"");
            q.Parameters.AddWithValue("sender",session.SenderCompId);q.Parameters.AddWithValue("target",session.TargetCompId);
            await using var row=await q.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)||row.GetInt32(0)!=session.SchemaVersion||!CryptographicOperations.FixedTimeEquals(row.GetFieldValue<byte[]>(1),OmsRecoveryRunCodec.Encode(session).Sha256)||
                row.GetInt64(2)<1||row.GetInt64(3)<1||await row.ReadAsync(ct))throw new InvalidOperationException();
        });
        await Check("CurrentEthAmendmentRiskPolicy",async()=>
        {
            var policy=await new OmsPostgresRiskPolicyReader(sources["Execution"]).ReadAsync(scope,ct);var now=DateTimeOffset.UtcNow;
            if(policy is null||now<policy.ValidFromUtc||now>policy.ValidThroughUtc||!policy.AllowedInstruments.Contains("ETH-PERPETUAL",StringComparer.Ordinal)||
                policy.Limits.MaxOrderUsd<1||policy.Limits.MaxPendingCommands<2||policy.Limits.MaxPendingUsd<2)throw new InvalidOperationException();
        });
        await Check("ExistingNodeObjectBinding",async()=>
        {
            var node=OmsExecutionNodeProvisioning.Inspect(root,scope);
            await using var q=sources["Execution"].CreateCommand("SELECT schema_version,node_sha256,lock_root,root_sha256,lock_sha256 FROM oms_execution_bindings_v2 WHERE venue_id=@v AND environment=@e");
            q.Parameters.AddWithValue("v",scope.Venue.Value);q.Parameters.AddWithValue("e",(short)scope.Environment);
            await using var row=await q.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)||row.GetInt32(0)!=2||row.GetString(1)!=node.NodeSha256||row.GetString(2)!=node.LockRoot||row.GetString(3)!=node.RootSha256||row.GetString(4)!=node.LockSha256||await row.ReadAsync(ct))throw new InvalidOperationException();
        });
        return findings;
        async Task Check(string code,Func<Task> inspect)
        {
            try{ct.ThrowIfCancellationRequested();await inspect();findings.Add(new(code,true));}
            catch(Exception error){findings.Add(new(code,false,Reason(error)));}
        }
    }
    private static string Reason(Exception error)
    {
        for(Exception? inner=error;inner is not null;inner=inner.InnerException)
            if(inner is TimeoutException or OperationCanceledException)return "Timeout";
        return error switch{PostgresException {SqlState:"42501"}=>"PermissionDenied",FileNotFoundException or DirectoryNotFoundException=>"MissingObject",
            ArgumentException=>"InvalidConfiguration",NpgsqlException or IOException=>"Unavailable",_=>"Unproven"};
    }
}
