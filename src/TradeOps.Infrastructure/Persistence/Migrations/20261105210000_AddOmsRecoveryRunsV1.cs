using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261105210000_AddOmsRecoveryRunsV1")]
public sealed class AddOmsRecoveryRunsV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE oms_recovery_runs_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL CHECK (venue_id='DERIBIT'),
 environment smallint NOT NULL CHECK (environment IN (3,4)), account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL DEFAULT '', run_id uuid NOT NULL, epoch uuid NOT NULL,
 generation bigint NOT NULL CHECK (generation>0), schema_version integer NOT NULL CHECK (schema_version=1),
 start_json jsonb NOT NULL, start_sha256 bytea NOT NULL CHECK (octet_length(start_sha256)=32),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id),
 UNIQUE (tenant_id,venue_id,environment,account_id,subaccount_id,generation)
);
CREATE TABLE oms_recovery_heads_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', run_id uuid NOT NULL,
 state varchar(16) NOT NULL CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked')),
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
  REFERENCES oms_recovery_runs_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TABLE oms_recovery_events_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', run_id uuid NOT NULL,
 event_id bigint GENERATED ALWAYS AS IDENTITY,
 state varchar(16) NOT NULL CHECK (state IN ('Disconnected','Recovering','Reconciling','Blocked')),
 reason varchar(32) NOT NULL CHECK (reason IN ('Begin','Reconcile','CompletedWithIssues','Cancelled','Failed','ConnectionLost','CaptureGap','AccountChanged')),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id,event_id),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
  REFERENCES oms_recovery_runs_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TABLE oms_deribit_recovery_results_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', run_id uuid NOT NULL,
 schema_version integer NOT NULL CHECK (schema_version=1), document_json jsonb NOT NULL,
 document_sha256 bytea NOT NULL CHECK (octet_length(document_sha256)=32),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
  REFERENCES oms_recovery_runs_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TRIGGER tr_recovery_runs_v1_immutable BEFORE UPDATE OR DELETE ON oms_recovery_runs_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_runs_v1_no_truncate BEFORE TRUNCATE ON oms_recovery_runs_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_events_v1_immutable BEFORE UPDATE OR DELETE ON oms_recovery_events_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_events_v1_no_truncate BEFORE TRUNCATE ON oms_recovery_events_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_results_v1_immutable BEFORE UPDATE OR DELETE ON oms_deribit_recovery_results_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_results_v1_no_truncate BEFORE TRUNCATE ON oms_deribit_recovery_results_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_recovery_runs_v1)
 THEN RAISE EXCEPTION 'Refusing destructive recovery rollback: preserve run evidence and issues'; END IF;
END; $$;
DROP TABLE oms_deribit_recovery_results_v1;
DROP TABLE oms_recovery_events_v1;
DROP TABLE oms_recovery_heads_v1;
DROP TABLE oms_recovery_runs_v1;
""");
}
