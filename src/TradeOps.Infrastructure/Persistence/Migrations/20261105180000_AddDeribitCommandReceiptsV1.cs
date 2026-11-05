using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261105180000_AddDeribitCommandReceiptsV1")]
public sealed class AddDeribitCommandReceiptsV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE oms_deribit_command_receipts_v1 (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL CHECK (venue_id='DERIBIT'),
 environment smallint NOT NULL CHECK (environment IN (3,4)),
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL DEFAULT '',
 command_id uuid NOT NULL,
 aggregate_id uuid NOT NULL,
 attempt_number integer NOT NULL CHECK (attempt_number=1),
 schema_version integer NOT NULL CHECK (schema_version=1),
 command_kind varchar(16) NOT NULL CHECK (command_kind IN ('Place','Cancel')),
 session_id uuid NOT NULL,
 rpc_request_id bigint NOT NULL CHECK (rpc_request_id>0),
 currency varchar(32) NOT NULL,
 native_order_id varchar(160) NOT NULL,
 native_instrument varchar(160) NOT NULL,
 command_sha256 bytea NOT NULL CHECK (octet_length(command_sha256)=32),
 receipt_sha256 bytea NOT NULL CHECK (octet_length(receipt_sha256)=32),
 receipt_json jsonb NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 UNIQUE (tenant_id,venue_id,environment,account_id,subaccount_id,session_id,rpc_request_id),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,command_id,attempt_number)
  REFERENCES oms_attempts(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,attempt_number),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
  REFERENCES oms_aggregates(tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
);
CREATE UNIQUE INDEX ux_deribit_receipt_placement_native ON oms_deribit_command_receipts_v1
 (tenant_id,venue_id,environment,account_id,subaccount_id,currency,native_order_id) WHERE command_kind='Place';
CREATE TRIGGER tr_deribit_receipts_v1_immutable BEFORE UPDATE OR DELETE ON oms_deribit_command_receipts_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_deribit_receipts_v1_no_truncate BEFORE TRUNCATE ON oms_deribit_command_receipts_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_deribit_command_receipts_v1)
 THEN RAISE EXCEPTION 'Refusing destructive Deribit receipt rollback: preserve native command evidence'; END IF;
END; $$;
DROP TABLE oms_deribit_command_receipts_v1;
""");
}
