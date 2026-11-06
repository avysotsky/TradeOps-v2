using Npgsql;
using TradeOps.Domain.Oms;
namespace TradeOps.Infrastructure.Persistence.Oms;

public static class OmsPostgresExecutionRuntimeGuard
{
    public static async Task ValidateAsync(NpgsqlDataSource source,CancellationToken ct=default,AccountKey? scope=null)
    {
        await using var q=source.CreateCommand("""
SELECT NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls))
AND NOT has_database_privilege(current_user,current_database(),'CREATE') AND NOT has_schema_privilege(current_user,'public','CREATE')
AND NOT EXISTS(SELECT 1 FROM (VALUES ('oms_account_risk_policies_v1'),('oms_execution_bindings_v1'),('oms_execution_bindings_v2'),('oms_risk_control_permissions_v1'),
 ('oms_risk_control_events_v1'),('oms_inbox'),('oms_private_capture_observations'),('oms_recovery_heads_v1'),('oms_recovered_account_states_v1')) t(relation)
 WHERE has_table_privilege(current_user,'public.'||relation,'INSERT,UPDATE,DELETE,TRUNCATE'))
AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN('r','p')
 AND has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE'))
AND (SELECT bool_and(has_table_privilege(current_user,'public.'||relation,'SELECT') AND has_table_privilege(current_user,'public.'||relation,'INSERT')) FROM (VALUES
 ('oms_aggregates'),('oms_intents'),('oms_commands'),('oms_attempts'),('oms_outbox'),('oms_integrity_incidents'),
 ('oms_deribit_command_receipts_v1'),('oms_deribit_fix_command_bindings_v1'),('oms_risk_reservations_v1'),('oms_risk_settlements_v1'),('oms_risk_admission_controls_v1'),('oms_risk_dispatches_v1'),('oms_risk_admission_audit_v1'),('oms_risk_cancel_admissions_v1'),('oms_execution_dispatches_v2')) t(relation))
AND (SELECT bool_and(has_table_privilege(current_user,'public.'||relation,'SELECT')) FROM (VALUES
 ('oms_recovery_heads_v1'),('oms_recovery_runs_v1'),('oms_recovered_account_states_v1'),('oms_evidence_versions_v1'),('oms_risk_control_events_v1'),('oms_account_risk_policies_v1'),('oms_execution_bindings_v2'),('oms_deribit_fix_reports_v1'),('oms_deribit_fix_native_events_v1'),('oms_deribit_fix_report_incidents_v1')) t(relation))
AND (SELECT bool_and(has_table_privilege(current_user,'public.'||relation,'UPDATE')) FROM (VALUES
 ('oms_aggregates'),('oms_intents'),('oms_commands'),('oms_attempts'),('oms_outbox')) t(relation))
AND NOT has_any_column_privilege(current_user,'public.oms_deribit_fix_command_bindings_v1','UPDATE')
AND NOT has_table_privilege(current_user,'public.oms_evidence_versions_v1','UPDATE')
AND NOT has_column_privilege(current_user,'public.oms_evidence_versions_v1','version','INSERT')
AND has_function_privilege(current_user,'public.oms_lock_evidence_version_v1(text,text,smallint,text,text)','EXECUTE')
AND has_function_privilege(current_user,'public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text)','EXECUTE')
AND has_function_privilege(current_user,'public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text)','EXECUTE')
AND has_function_privilege(current_user,'public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text)','EXECUTE')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','tenant_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','venue_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','environment','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','account_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','subaccount_id','INSERT')
AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN('r','p')
 AND c.relname NOT IN ('oms_aggregates','oms_intents','oms_commands','oms_attempts','oms_outbox','oms_integrity_incidents',
 'oms_deribit_command_receipts_v1','oms_deribit_fix_command_bindings_v1','oms_risk_reservations_v1','oms_risk_settlements_v1','oms_risk_admission_controls_v1','oms_risk_dispatches_v1','oms_risk_admission_audit_v1','oms_risk_cancel_admissions_v1','oms_execution_dispatches_v2','oms_evidence_versions_v1')
 AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE'))
AND has_sequence_privilege(current_user,'public.oms_risk_reservations_v1_reservation_id_seq','USAGE')
""");
        if(await q.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Restricted execution role required; startup refused.");
        if(scope is not null)
        {
            await using var permission=source.CreateCommand("""
SELECT EXISTS(SELECT 1 FROM oms_risk_control_permissions_v1 p JOIN pg_roles r ON r.rolname=p.database_role
 WHERE p.level=3 AND p.kind=2 AND p.tenant_id=@t AND p.venue_id=@v AND p.environment=@e AND p.account_id=@a AND p.subaccount_id=@s
 AND p.can_activate AND pg_has_role(current_user,r.oid,'MEMBER'))
AND NOT EXISTS(SELECT 1 FROM oms_risk_control_permissions_v1 p JOIN pg_roles r ON r.rolname=p.database_role WHERE p.can_clear AND pg_has_role(current_user,r.oid,'MEMBER'))
""");
            permission.Parameters.AddWithValue("t",scope.Tenant.Value);permission.Parameters.AddWithValue("v",scope.Venue.Value);permission.Parameters.AddWithValue("e",(short)scope.Environment);permission.Parameters.AddWithValue("a",scope.Account.Value);permission.Parameters.AddWithValue("s",scope.Subaccount?.Value??"");
            if(await permission.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("Exact activate-only account circuit permission required.");
        }
    }
}
