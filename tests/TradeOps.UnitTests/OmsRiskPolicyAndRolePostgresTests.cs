using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using FrozenClock=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;
public sealed class OmsRiskPolicyAndRolePostgresTests
{
    [Fact]
    public async Task PolicyReadIsScopedLatestHashedAndMissingPolicyNeverFallsBackToDefaults()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var scope=Scope();var reader=new OmsPostgresRiskPolicyReader(db.Source);
        Assert.Null(await reader.ReadAsync(scope));
        var p=DeribitRiskInputsTests.Policy() with{Limits=DeribitRiskInputsTests.Policy().Limits with{Scope=scope}};
        await InsertPolicy(db,p,1);Assert.Equal("1",(await reader.ReadAsync(scope))!.Limits.Revision);Assert.Null(await reader.ReadAsync(Scope("another")));
        await InsertPolicy(db,p with{Limits=p.Limits with{Revision="2"},ValidThroughUtc=DateTimeOffset.UnixEpoch},2);
        Assert.Equal(DateTimeOffset.UnixEpoch,(await reader.ReadAsync(scope))!.ValidThroughUtc);
        await InsertPolicy(db,p with{Limits=p.Limits with{Revision="3",SchemaVersion=2}},3);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>reader.ReadAsync(scope));
        await using var q=db.Source.CreateCommand("DELETE FROM oms_account_risk_policies_v1");await Assert.ThrowsAsync<PostgresException>(()=>q.ExecuteNonQueryAsync());
    }
    internal static async Task InsertPolicy(OmsPostgresTestDatabase db,OmsRiskPolicyDocumentV1 p,long revision)
    {
        var encoded=OmsRecoveryRunCodec.Encode(p);var s=p.Limits.Scope;
        await using var q=db.Source.CreateCommand("INSERT INTO oms_account_risk_policies_v1(tenant_id,venue_id,environment,account_id,subaccount_id,revision,schema_version,policy_json,policy_sha256) VALUES(@t,@v,@e,@a,@s,@r,@schema,@json,@sha)");
        q.Parameters.AddWithValue("schema",p.SchemaVersion);
        q.Parameters.AddWithValue("t",s.Tenant.Value);q.Parameters.AddWithValue("v",s.Venue.Value);q.Parameters.AddWithValue("e",(short)s.Environment);q.Parameters.AddWithValue("a",s.Account.Value);q.Parameters.AddWithValue("s",s.Subaccount?.Value??"");q.Parameters.AddWithValue("r",revision);q.Parameters.AddWithValue("json",NpgsqlDbType.Jsonb,encoded.Json);q.Parameters.AddWithValue("sha",encoded.Sha256);await q.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task ActualRestrictedExecutionRoleCanDispatchWithoutUpdatingRecoveryHeadOrProvisioningItself()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        var role="to_exec_"+Guid.NewGuid().ToString("N");
        await using(var q=db.Source.CreateCommand($"""
CREATE ROLE "{role}" NOLOGIN; COMMENT ON ROLE "{role}" IS 'tradeops-tests:role:v1:{db.Name}';
GRANT USAGE ON SCHEMA public TO "{role}";
GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{role}";
GRANT INSERT ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox,oms_integrity_incidents,oms_deribit_command_receipts_v1,oms_deribit_fix_command_bindings_v1,
 oms_risk_reservations_v1,oms_risk_settlements_v1,oms_risk_admission_controls_v1,oms_risk_dispatches_v1,oms_risk_admission_audit_v1,oms_risk_cancel_admissions_v1,oms_execution_dispatches_v2 TO "{role}";
GRANT UPDATE ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox TO "{role}";
GRANT INSERT(tenant_id,venue_id,environment,account_id,subaccount_id) ON oms_evidence_versions_v1 TO "{role}";
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO "{role}";
GRANT EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text),
 public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text),public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text),
 public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text) TO "{role}";
INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',3,'account','',2,'{role}',true,false);
"""))await q.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Pooling=false,Options="-c role="+role}.ConnectionString);
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(source,scope:Scope());
            var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await OmsExecutionGatewayPostgresTests.Prepare(live);var wire=new OmsExecutionGatewayPostgresTests.Wire();
            await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
            Assert.Equal(OmsExecutionResultKind.Acknowledged,(await gateway.SubmitAsync(OmsExecutionGatewayPostgresTests.Command())).Kind);
            Assert.Equal(1,wire.Writes);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresExecutionRuntimeGuard.ValidateAsync(db.Source));
            await using var denied=source.CreateCommand("UPDATE oms_recovery_heads_v1 SET state='Ready'");Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        }
        finally
        {await using var q=db.Source.CreateCommand($"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM \"{role}\"; REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM \"{role}\"; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public FROM \"{role}\"; REVOKE USAGE ON SCHEMA public FROM \"{role}\"; DROP ROLE \"{role}\"");await q.ExecuteNonQueryAsync();}
    }
    [Fact]
    public async Task DailyLossActivatesDurableCircuitThroughRealRoleAndCannotAutoClear()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();
        // Dedicated test admin is explicitly provisioned for this one scope, exactly like a runtime activate-only role.
        await using(var q=db.Source.CreateCommand("INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',3,'account','',2,current_user,true,false)"))await q.ExecuteNonQueryAsync();
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var r=await OmsExecutionGatewayPostgresTests.Prepare(live);
        var risk=new OmsPostgresRiskAdmission(db.Source,new Inputs(clock){Transform=x=>x with{Valuation=x.Valuation with{DailyLossUsd=6}}},clock);
        Assert.Equal(OmsRiskAdmissionResult.Denied,await risk.AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),r.Current!.Publication,r.Current.Qualified));
        Assert.Equal(OmsRiskAdmissionResult.ControlBlocked,await new OmsPostgresRiskAdmission(db.Source,new Inputs(clock),clock).AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),r.Current.Publication,r.Current.Qualified));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_control_events_v1"));
    }
}
