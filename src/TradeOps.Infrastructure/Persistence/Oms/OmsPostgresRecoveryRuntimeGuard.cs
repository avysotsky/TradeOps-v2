using Npgsql;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Validates an already-provisioned recovery role. Never migrates, grants or falls back to admin.</summary>
public static class OmsPostgresRecoveryRuntimeGuard
{
    public static async Task ValidateAsync(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
SELECT NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user
 AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls))
AND NOT has_database_privilege(current_user,current_database(),'CREATE')
AND NOT has_schema_privilege(current_user,'public','CREATE')
AND (SELECT bool_and(has_table_privilege(current_user,relation,privilege)) FROM (VALUES
 ('public.oms_aggregates','SELECT'),('public.oms_intents','SELECT'),('public.oms_commands','SELECT'),
 ('public.oms_outbox','SELECT'),('public.oms_attempts','SELECT'),('public.oms_inbox','SELECT'),
 ('public.oms_private_capture_observations','SELECT'),('public.oms_integrity_incidents','SELECT'),
 ('public.oms_deribit_fix_command_bindings_v1','SELECT'),('public.oms_deribit_fix_reports_v1','SELECT'),('public.oms_deribit_fix_native_events_v1','SELECT'),('public.oms_deribit_fix_report_incidents_v1','SELECT'),('public.oms_deribit_command_receipts_v1','SELECT'),('public.oms_recovery_external_reviews_v1','SELECT'),
 ('public.oms_evidence_versions_v1','SELECT'),('public.oms_recovery_runs_v1','SELECT'),
 ('public.oms_recovery_heads_v1','SELECT'),('public.oms_recovery_events_v1','SELECT'),
 ('public.oms_deribit_recovery_results_v1','SELECT'),('public.oms_recovered_account_states_v1','SELECT'),
 ('public.oms_commands','UPDATE'),('public.oms_attempts','UPDATE'),('public.oms_outbox','UPDATE'),
 ('public.oms_recovery_runs_v1','INSERT'),('public.oms_recovery_heads_v1','INSERT'),('public.oms_recovery_heads_v1','UPDATE'),
 ('public.oms_recovery_events_v1','INSERT'),('public.oms_deribit_recovery_results_v1','INSERT'),('public.oms_recovered_account_states_v1','INSERT')
) required(relation,privilege))
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','tenant_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','venue_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','environment','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','account_id','INSERT')
AND has_column_privilege(current_user,'public.oms_evidence_versions_v1','subaccount_id','INSERT')
AND has_sequence_privilege(current_user,'public.oms_recovery_events_v1_event_id_seq','USAGE')
AND has_function_privilege(current_user,'public.oms_lock_evidence_version_v1(text,text,smallint,text,text)','EXECUTE')
AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
 AND c.relname LIKE 'oms\_%' ESCAPE '\' AND has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE'))
AND NOT EXISTS (SELECT 1 FROM (VALUES ('oms_commands'),('oms_attempts'),('oms_outbox'),('oms_aggregates'),('oms_intents'),
 ('oms_inbox'),('oms_deribit_command_receipts_v1'),('oms_private_capture_observations'),('oms_integrity_incidents'),
 ('oms_recovery_external_reviews_v1'),('oms_recovery_review_permissions_v1')) t(relation)
 WHERE has_table_privilege(current_user,'public.'||relation,'INSERT'))
AND NOT has_table_privilege(current_user,'public.oms_evidence_versions_v1','UPDATE')
AND NOT has_column_privilege(current_user,'public.oms_evidence_versions_v1','version','INSERT')
AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
 AND c.relkind IN ('r','p') AND c.relname NOT IN ('oms_commands','oms_attempts','oms_outbox','oms_recovery_runs_v1',
 'oms_recovery_heads_v1','oms_recovery_events_v1','oms_deribit_recovery_results_v1','oms_recovered_account_states_v1','oms_evidence_versions_v1')
 AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE'))
""", db);
        if (await query.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Restricted recovery database role required; startup refused.");
    }
}
