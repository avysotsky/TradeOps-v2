using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106170000_AddOmsExecutionBindingV1")]
public sealed class AddOmsExecutionBindingV1 : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
-- Explicit single-host deployment binding. A runtime role cannot migrate/repin the physical owner to another host or lock root.
CREATE TABLE oms_execution_bindings_v1 (
 venue_id varchar(64) NOT NULL, environment smallint NOT NULL, schema_version integer NOT NULL CHECK(schema_version=1),
 node_sha256 varchar(64) NOT NULL CHECK(node_sha256 ~ '^[0-9A-F]{64}$'), lock_root text NOT NULL,
 PRIMARY KEY(venue_id,environment)
);
CREATE TRIGGER tr_execution_bindings_v1_immutable BEFORE UPDATE OR DELETE ON oms_execution_bindings_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_execution_bindings_v1_no_truncate BEFORE TRUNCATE ON oms_execution_bindings_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TABLE oms_risk_dispatches_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL, command_id uuid NOT NULL,
 schema_version integer NOT NULL CHECK(schema_version=1), evidence_after_started bigint NOT NULL CHECK(evidence_after_started>0),
 owner_id uuid NOT NULL, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
 REFERENCES oms_risk_admission_controls_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);
CREATE TRIGGER tr_risk_dispatches_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_dispatches_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_dispatches_v1_no_truncate BEFORE TRUNCATE ON oms_risk_dispatches_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE FUNCTION public.oms_lock_risk_recovery_head_v1(t text,v text,e smallint,a text,s text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor name; actual uuid;
BEGIN
 actor:=CASE WHEN current_setting('role')='none' THEN session_user ELSE current_setting('role') END;
 IF NOT has_table_privilege(actor,'public.oms_recovery_heads_v1','SELECT') OR
 NOT has_table_privilege(actor,'public.oms_risk_reservations_v1','INSERT') THEN RAISE EXCEPTION 'Risk recovery lock permission required' USING ERRCODE='42501'; END IF;
 SELECT h.run_id INTO actual FROM public.oms_recovery_heads_v1 h WHERE h.tenant_id=t AND h.venue_id=v
 AND h.environment=e AND h.account_id=a AND h.subaccount_id=s AND h.state='Ready' FOR UPDATE;
 RETURN actual;
END $$;
REVOKE ALL ON FUNCTION public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text) FROM PUBLIC;
""");
    protected override void Down(MigrationBuilder b) => b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_execution_bindings_v1) OR EXISTS(SELECT 1 FROM oms_risk_dispatches_v1) THEN RAISE EXCEPTION 'Preserve execution ownership audit'; END IF; END $$;
DROP TABLE oms_risk_dispatches_v1; DROP TABLE oms_execution_bindings_v1;
DROP FUNCTION public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text);
""");
}
