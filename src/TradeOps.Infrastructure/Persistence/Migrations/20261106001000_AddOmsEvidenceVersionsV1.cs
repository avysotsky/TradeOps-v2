using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106001000_AddOmsEvidenceVersionsV1")]
public sealed class AddOmsEvidenceVersionsV1 : Migration
{
    private static readonly string[] EvidenceTables = ["oms_aggregates", "oms_intents", "oms_commands", "oms_outbox", "oms_attempts",
        "oms_inbox", "oms_private_capture_observations", "oms_integrity_incidents", "oms_deribit_command_receipts_v1"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE oms_evidence_versions_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL CHECK (environment BETWEEN 1 AND 4),
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '',
 version bigint NOT NULL DEFAULT 0 CHECK (version>=0),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id)
);
CREATE FUNCTION public.oms_advance_evidence_version_v1() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE scoped record;
BEGIN
 IF TG_OP='DELETE' THEN scoped:=OLD; ELSE scoped:=NEW; END IF;
 INSERT INTO public.oms_evidence_versions_v1 (tenant_id,venue_id,environment,account_id,subaccount_id,version)
 VALUES (scoped.tenant_id,scoped.venue_id,scoped.environment,scoped.account_id,scoped.subaccount_id,1)
 ON CONFLICT (tenant_id,venue_id,environment,account_id,subaccount_id)
 DO UPDATE SET version=public.oms_evidence_versions_v1.version+1;
 RETURN NULL;
END; $$;
REVOKE ALL ON FUNCTION public.oms_advance_evidence_version_v1() FROM PUBLIC;
-- Row-lock arbitration without granting a runtime role UPDATE on the evidence counter.
CREATE FUNCTION public.oms_lock_evidence_version_v1(t text,v text,e smallint,a text,s text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor name; actual bigint;
BEGIN
 actor := CASE WHEN current_setting('role')='none' THEN session_user ELSE current_setting('role') END;
 IF NOT has_table_privilege(actor,'public.oms_evidence_versions_v1','SELECT') OR
  NOT has_column_privilege(actor,'public.oms_evidence_versions_v1','tenant_id','INSERT') OR
  NOT has_column_privilege(actor,'public.oms_evidence_versions_v1','venue_id','INSERT') OR
  NOT has_column_privilege(actor,'public.oms_evidence_versions_v1','environment','INSERT') OR
  NOT has_column_privilege(actor,'public.oms_evidence_versions_v1','account_id','INSERT') OR
  NOT has_column_privilege(actor,'public.oms_evidence_versions_v1','subaccount_id','INSERT') THEN
  RAISE EXCEPTION 'Restricted evidence arbitration permission required' USING ERRCODE='42501';
 END IF;
 INSERT INTO public.oms_evidence_versions_v1(tenant_id,venue_id,environment,account_id,subaccount_id)
 VALUES(t,v,e,a,s) ON CONFLICT DO NOTHING;
 SELECT version INTO actual FROM public.oms_evidence_versions_v1 c WHERE c.tenant_id=t AND c.venue_id=v
  AND c.environment=e AND c.account_id=a AND c.subaccount_id=s FOR UPDATE;
 RETURN actual;
END; $$;
REVOKE ALL ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text) FROM PUBLIC;
CREATE TRIGGER tr_evidence_versions_v1_no_delete BEFORE DELETE ON oms_evidence_versions_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_evidence_versions_v1_no_truncate BEFORE TRUNCATE ON oms_evidence_versions_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");
        foreach (var table in EvidenceTables)
            migrationBuilder.Sql($"CREATE TRIGGER tr_evidence_version_v1 AFTER INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION public.oms_advance_evidence_version_v1();");
        // Existing evidence is represented too. Trigger installation holds each evidence table's DDL lock
        // until this migration commits; no timestamp/max(sequence) is used as a commit cursor.
        migrationBuilder.Sql("INSERT INTO oms_evidence_versions_v1 (tenant_id,venue_id,environment,account_id,subaccount_id,version) SELECT tenant_id,venue_id,environment,account_id,subaccount_id,1 FROM (" +
            string.Join(" UNION ", EvidenceTables.Select(t => $"SELECT tenant_id,venue_id,environment,account_id,subaccount_id FROM {t}")) + ") scopes ON CONFLICT DO NOTHING;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_recovery_runs_v1)
 THEN RAISE EXCEPTION 'Refusing destructive evidence-version rollback: preserve scoped recovery history'; END IF;
END; $$;
""");
        foreach (var table in EvidenceTables) migrationBuilder.Sql($"DROP TRIGGER tr_evidence_version_v1 ON {table};");
        migrationBuilder.Sql("DROP FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text); DROP FUNCTION public.oms_advance_evidence_version_v1(); DROP TABLE oms_evidence_versions_v1;");
    }
}
