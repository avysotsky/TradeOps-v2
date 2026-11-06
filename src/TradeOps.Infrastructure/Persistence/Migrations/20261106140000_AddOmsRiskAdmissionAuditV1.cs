using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106140000_AddOmsRiskAdmissionAuditV1")]
public sealed class AddOmsRiskAdmissionAuditV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE oms_risk_admission_audit_v1 (
 audit_id uuid PRIMARY KEY, schema_version integer NOT NULL CHECK(schema_version=1),
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL, command_sha256 bytea NOT NULL CHECK(octet_length(command_sha256)=32),
 result smallint NOT NULL CHECK(result BETWEEN 1 AND 7), baseline_run_id uuid NOT NULL,
 state_sha256 bytea NOT NULL CHECK(octet_length(state_sha256)=32), actor_role name NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_risk_admission_audit_v1_command ON oms_risk_admission_audit_v1
 (tenant_id,venue_id,environment,account_id,subaccount_id,command_id);
CREATE TRIGGER tr_risk_admission_audit_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_admission_audit_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_admission_audit_v1_no_truncate BEFORE TRUNCATE ON oms_risk_admission_audit_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM oms_risk_admission_audit_v1) THEN
 RAISE EXCEPTION 'Refusing destructive risk admission audit rollback'; END IF;
END $$;
DROP TABLE oms_risk_admission_audit_v1;
""");
}
