using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106010000_AddOmsRecoveredAccountStatesV1")]
public sealed class AddOmsRecoveredAccountStatesV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE oms_recovery_heads_v1 DROP CONSTRAINT oms_recovery_heads_v1_state_check;
ALTER TABLE oms_recovery_heads_v1 ADD CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked','Ready'));
ALTER TABLE oms_recovery_events_v1 DROP CONSTRAINT oms_recovery_events_v1_state_check;
ALTER TABLE oms_recovery_events_v1 ADD CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked','Ready'));
ALTER TABLE oms_recovery_events_v1 DROP CONSTRAINT oms_recovery_events_v1_reason_check;
ALTER TABLE oms_recovery_events_v1 ADD CHECK (reason IN ('Begin','Reconcile','CompletedWithIssues','Cancelled','Failed','ConnectionLost','CaptureGap','AccountChanged','QualifiedPublication'));
CREATE TABLE oms_recovered_account_states_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', run_id uuid NOT NULL,
 evidence_version bigint NOT NULL CHECK (evidence_version>=0),
 native_through_utc timestamptz NOT NULL,
 schema_version integer NOT NULL CHECK (schema_version=1), state_json jsonb NOT NULL,
 state_sha256 bytea NOT NULL CHECK (octet_length(state_sha256)=32),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
  REFERENCES oms_deribit_recovery_results_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TRIGGER tr_recovered_account_states_v1_immutable BEFORE UPDATE OR DELETE ON oms_recovered_account_states_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovered_account_states_v1_no_truncate BEFORE TRUNCATE ON oms_recovered_account_states_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_recovered_account_states_v1)
 THEN RAISE EXCEPTION 'Refusing destructive rollback of recovered account evidence'; END IF;
END; $$;
DROP TABLE oms_recovered_account_states_v1;
ALTER TABLE oms_recovery_heads_v1 DROP CONSTRAINT oms_recovery_heads_v1_state_check;
ALTER TABLE oms_recovery_heads_v1 ADD CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked'));
ALTER TABLE oms_recovery_events_v1 DROP CONSTRAINT oms_recovery_events_v1_state_check;
ALTER TABLE oms_recovery_events_v1 ADD CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked'));
ALTER TABLE oms_recovery_events_v1 DROP CONSTRAINT oms_recovery_events_v1_reason_check;
ALTER TABLE oms_recovery_events_v1 ADD CHECK (reason IN ('Begin','Reconcile','CompletedWithIssues','Cancelled','Failed','ConnectionLost','CaptureGap','AccountChanged'));
""");
}
