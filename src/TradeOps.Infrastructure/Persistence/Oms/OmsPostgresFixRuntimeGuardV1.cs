using Npgsql;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Already-provisioned protocol/native-capture identity, separate from execution
/// and recovery identities. No grants, session seeding, migration or administrator fallback.</summary>
public static class OmsPostgresFixRuntimeGuardV1
{
    public static async Task ValidateAsync(NpgsqlDataSource source,CancellationToken ct=default)
    {
        await using var q=source.CreateCommand("""
SELECT NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user
 AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls))
AND NOT has_database_privilege(current_user,current_database(),'CREATE')
AND NOT has_schema_privilege(current_user,'public','CREATE')
AND (SELECT bool_and(has_table_privilege(current_user,'public.'||relation,'SELECT')) FROM (VALUES
 ('oms_fix_sessions_v1'),('oms_fix_outbound_messages_v1'),('oms_deribit_fix_reports_v1'),
 ('oms_deribit_fix_native_events_v1'),('oms_deribit_fix_report_incidents_v1'),('oms_evidence_versions_v1')) t(relation))
AND (SELECT bool_and(has_table_privilege(current_user,'public.'||relation,'INSERT')) FROM (VALUES
 ('oms_fix_outbound_messages_v1'),('oms_deribit_fix_reports_v1'),
 ('oms_deribit_fix_native_events_v1'),('oms_deribit_fix_report_incidents_v1')) t(relation))
AND (SELECT bool_and(has_column_privilege(current_user,'public.oms_fix_sessions_v1',col,'UPDATE'))
 FROM (VALUES ('next_sender'),('next_target'),('last_logon_ms')) t(col))
AND NOT has_any_column_privilege(current_user,'public.oms_fix_sessions_v1','INSERT')
AND NOT EXISTS(SELECT 1 FROM information_schema.columns c WHERE c.table_schema='public' AND c.table_name='oms_fix_sessions_v1'
 AND c.column_name NOT IN('next_sender','next_target','last_logon_ms')
 AND has_column_privilege(current_user,'public.oms_fix_sessions_v1',c.column_name,'UPDATE'))
AND NOT has_column_privilege(current_user,'public.oms_evidence_versions_v1','version','INSERT')
AND (SELECT bool_and(has_column_privilege(current_user,'public.oms_evidence_versions_v1',col,'INSERT'))
 FROM (VALUES ('tenant_id'),('venue_id'),('environment'),('account_id'),('subaccount_id')) t(col))
AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='public' AND c.relkind IN('r','p') AND
 (has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE') OR
  c.relname NOT IN('oms_fix_sessions_v1') AND has_any_column_privilege(current_user,c.oid,'UPDATE') OR
  c.relname NOT IN('oms_fix_outbound_messages_v1','oms_deribit_fix_reports_v1','oms_deribit_fix_native_events_v1',
   'oms_deribit_fix_report_incidents_v1','oms_evidence_versions_v1') AND has_any_column_privilege(current_user,c.oid,'INSERT')))
""");
        if(await q.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("Separate restricted FIX protocol/capture role required; startup refused.");
    }
}
