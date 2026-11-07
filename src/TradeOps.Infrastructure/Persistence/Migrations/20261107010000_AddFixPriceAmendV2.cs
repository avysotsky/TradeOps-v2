using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261107010000_AddFixPriceAmendV2")]
public sealed class AddFixPriceAmendV2:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
ALTER TABLE oms_deribit_fix_command_bindings_v1 DROP CONSTRAINT oms_deribit_fix_command_bindings_v1_schema_version_check;
ALTER TABLE oms_deribit_fix_command_bindings_v1 ADD CONSTRAINT oms_deribit_fix_command_bindings_v1_schema_version_check CHECK(schema_version IN(1,2));
ALTER TABLE oms_risk_reservations_v1 DROP CONSTRAINT oms_risk_reservations_v1_schema_version_check;
ALTER TABLE oms_risk_reservations_v1 ADD CONSTRAINT oms_risk_reservations_v1_schema_version_check CHECK(schema_version IN(1,2));
CREATE TABLE oms_fix_amend_messages_v2 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 sender_comp_id varchar(64) NOT NULL,target_comp_id varchar(64) NOT NULL,sequence bigint NOT NULL CHECK(sequence BETWEEN 1 AND 999999999),
 schema_version integer NOT NULL CHECK(schema_version=2),message_type varchar(2) NOT NULL CHECK(message_type='G'),
 application_message text NOT NULL CHECK(octet_length(application_message)<=32768),application_sha256 bytea NOT NULL CHECK(octet_length(application_sha256)=32),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,sequence),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
 REFERENCES oms_fix_sessions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
);
CREATE TRIGGER tr_fix_amend_v2_immutable BEFORE UPDATE OR DELETE ON oms_fix_amend_messages_v2 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_fix_amend_v2_no_truncate BEFORE TRUNCATE ON oms_fix_amend_messages_v2 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_fix_amend_messages_v2) OR EXISTS(SELECT 1 FROM oms_deribit_fix_command_bindings_v1 WHERE schema_version=2)
OR EXISTS(SELECT 1 FROM oms_risk_reservations_v1 WHERE schema_version=2)
THEN RAISE EXCEPTION 'Preserve FIX amendment audit'; END IF; END $$;
DROP TABLE oms_fix_amend_messages_v2;
ALTER TABLE oms_risk_reservations_v1 DROP CONSTRAINT oms_risk_reservations_v1_schema_version_check;
ALTER TABLE oms_risk_reservations_v1 ADD CONSTRAINT oms_risk_reservations_v1_schema_version_check CHECK(schema_version=1);
ALTER TABLE oms_deribit_fix_command_bindings_v1 DROP CONSTRAINT oms_deribit_fix_command_bindings_v1_schema_version_check;
ALTER TABLE oms_deribit_fix_command_bindings_v1 ADD CONSTRAINT oms_deribit_fix_command_bindings_v1_schema_version_check CHECK(schema_version=1);
""");
}
