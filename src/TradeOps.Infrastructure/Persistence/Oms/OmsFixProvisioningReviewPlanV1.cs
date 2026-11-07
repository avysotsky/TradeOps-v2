using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed record OmsFixProvisioningReviewPlanV1(string FromMigration,string ToMigration,int MigrationCount,
    string MigrationSql,string RolesAndSessionSql,OmsFixSessionKeyV1 Session);

/// <summary>Produces review text only. Database connections are explicitly prohibited.</summary>
public static class OmsFixProvisioningReviewPlan
{
    public static readonly IReadOnlyList<string> Roles=Array.AsReadOnly(new[]{"tradeops_fix_capture_v1","tradeops_fix_recovery_v1","tradeops_fix_execution_v1","tradeops_fix_protocol_v1"});
    public static OmsFixProvisioningReviewPlanV1 Generate(string[] applied,AccountKey scope,string database,string marker,string cluster)
    {
        if(scope.Environment!=OmsEnvironment.Mainnet||scope.Venue.Value!="DERIBIT"||applied.Length==0)throw new ArgumentException("Retained account scope required.");
        if(new[]{database,marker,cluster}.Any(x=>string.IsNullOrWhiteSpace(x)||x.Any(c=>c<' '||c=='$')))
            throw new ArgumentException("Bounded provisioning target identity required.");
        var options=new DbContextOptionsBuilder<TradeOpsDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=offline;Timeout=1",x=>x.SetPostgresVersion(17,11))
            .AddInterceptors(new OfflineOnly()).Options;
        using var context=new TradeOpsDbContext(options);var known=context.Database.GetMigrations().ToArray();
        if(!known.Take(applied.Length).SequenceEqual(applied)||applied.Length>=known.Length)throw new ArgumentException("Exact older migration prefix required.");
        var sql=context.GetService<IMigrator>().GenerateScript(applied[^1],known[^1],MigrationsSqlGenerationOptions.Idempotent);
        var hash=Convert.ToHexString(OmsRecoveryRunCodec.Encode(scope).Sha256).ToLowerInvariant();
        var session=new OmsFixSessionKeyV1(scope.Subaccount is null?1:2,scope,"tradeops-fix-q-"+hash[..24],"DERIBITSERVER");session.Validate();
        var guard=$"""
DO $guard$ BEGIN
 IF current_database()<>{L(database)} OR current_setting('cluster_name')<>{L(cluster)} OR
 (SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()) IS DISTINCT FROM {L(marker)}
 THEN RAISE EXCEPTION 'Wrong provisioning target'; END IF;
END $guard$;
""";
        var c=Q(Roles[0]);var r=Q(Roles[1]);var e=Q(Roles[2]);var f=Q(Roles[3]);
        var roles="BEGIN;\n"+guard+"\n"+HistoryGuard(known)+"\n"+string.Join("\n",Roles.Select(name=>$"""
DO $role$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname={L(name)}) THEN RAISE EXCEPTION 'Provisioning role already exists; inspect before reuse'; END IF; END $role$;
CREATE ROLE {Q(name)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
COMMENT ON ROLE {Q(name)} IS 'tradeops-fix-qualification:runtime:v1';
GRANT CONNECT ON DATABASE {Q(database)} TO {Q(name)};
GRANT USAGE ON SCHEMA public TO {Q(name)};
"""))+$"""

GRANT SELECT,INSERT,UPDATE ON oms_inbox,oms_private_capture_observations TO {c};
GRANT SELECT,INSERT ON oms_integrity_incidents TO {c};
GRANT USAGE ON SEQUENCE oms_integrity_incidents_incident_id_seq TO {c};
DO $reads$ DECLARE t record; BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename LIKE 'oms\_%' ESCAPE '\' LOOP
  EXECUTE format('GRANT SELECT ON public.%I TO %I,%I',t.tablename,{L(Roles[1])},{L(Roles[2])});
 END LOOP;
END $reads$;
GRANT UPDATE ON oms_commands,oms_attempts,oms_outbox,oms_recovery_heads_v1 TO {r};
GRANT INSERT ON oms_recovery_runs_v1,oms_recovery_heads_v1,oms_recovery_events_v1,oms_deribit_recovery_results_v1,oms_recovered_account_states_v1 TO {r};
GRANT USAGE ON SEQUENCE oms_recovery_events_v1_event_id_seq TO {r};
GRANT EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text) TO {r};
GRANT INSERT ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox,oms_integrity_incidents,oms_deribit_command_receipts_v1,
 oms_risk_reservations_v1,oms_risk_settlements_v1,oms_risk_admission_controls_v1,oms_risk_dispatches_v1,oms_risk_admission_audit_v1,
 oms_risk_cancel_admissions_v1,oms_execution_dispatches_v2,oms_deribit_fix_command_bindings_v1 TO {e};
GRANT UPDATE ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox TO {e};
GRANT USAGE ON SEQUENCE oms_risk_reservations_v1_reservation_id_seq TO {e};
GRANT EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text),
 public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text),public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text),
 public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text) TO {e};
GRANT SELECT ON oms_fix_sessions_v1,oms_fix_outbound_messages_v1,oms_fix_amend_messages_v2,oms_deribit_fix_reports_v1,
 oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1,oms_evidence_versions_v1 TO {f};
GRANT INSERT ON oms_fix_outbound_messages_v1,oms_fix_amend_messages_v2,oms_deribit_fix_reports_v1,oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1 TO {f};
GRANT UPDATE(next_sender,next_target,last_logon_ms) ON oms_fix_sessions_v1 TO {f};
GRANT INSERT(tenant_id,venue_id,environment,account_id,subaccount_id) ON oms_evidence_versions_v1 TO {r},{e},{f};
INSERT INTO oms_risk_control_permissions_v1 VALUES(3,{L(scope.Tenant.Value)},'DERIBIT',4,{L(scope.Account.Value)},{L(scope.Subaccount?.Value??"")},2,{L(Roles[2])},true,false);
INSERT INTO oms_fix_sessions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,schema_version,key_sha256)
VALUES({L(scope.Tenant.Value)},'DERIBIT',4,{L(scope.Account.Value)},{L(scope.Subaccount?.Value??"")},{L(session.SenderCompId)},'DERIBITSERVER',{session.SchemaVersion},decode('{Convert.ToHexString(OmsRecoveryRunCodec.Encode(session).Sha256)}','hex'));
COMMIT;
""";
        // No policy defaults, login passwords, binding rewrite or business-evidence repair.
        return new(applied[^1],known[^1],known.Length-applied.Length,guard+"\n"+HistoryGuard(applied,known)+"\n"+sql,roles,session);
    }
    private static string HistoryGuard(params string[][] histories)=>"DO $history$ BEGIN IF "+
        string.Join(" AND ",histories.Select(h=>"ARRAY(SELECT \"MigrationId\"::text FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\") IS DISTINCT FROM ARRAY["+string.Join(",",h.Select(L))+"]::text[]"))+
        " THEN RAISE EXCEPTION 'Migration history changed; inspect before provisioning'; END IF; END $history$;";
    private static string L(string value)=>"'"+value.Replace("'","''",StringComparison.Ordinal)+"'";
    private static string Q(string value)=>"\""+value.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
    private sealed class OfflineOnly:DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection,ConnectionEventData eventData,InterceptionResult result)=>throw new InvalidOperationException("Review generation cannot open a database.");
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,ConnectionEventData eventData,InterceptionResult result,CancellationToken ct=default)=>throw new InvalidOperationException("Review generation cannot open a database.");
    }
}
