using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106220000_AddDeribitFixCommandBindingsV1")]
public sealed class AddDeribitFixCommandBindingsV1:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
CREATE TABLE oms_deribit_fix_command_bindings_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL,schema_version integer NOT NULL CHECK(schema_version=1),
 binding_json jsonb NOT NULL CHECK(octet_length(binding_json::text)<=65536),binding_sha256 bytea NOT NULL CHECK(octet_length(binding_sha256)=32),
 command_sha256 bytea NOT NULL CHECK(octet_length(command_sha256)=32),recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
 REFERENCES oms_commands(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);
CREATE TRIGGER tr_fix_binding_immutable BEFORE UPDATE OR DELETE ON oms_deribit_fix_command_bindings_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_fix_binding_no_truncate BEFORE TRUNCATE ON oms_deribit_fix_command_bindings_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_evidence_version_v1 AFTER INSERT ON oms_deribit_fix_command_bindings_v1 FOR EACH ROW EXECUTE FUNCTION public.oms_advance_evidence_version_v1();
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_deribit_fix_command_bindings_v1) THEN RAISE EXCEPTION 'Preserve native FIX command binding'; END IF; END $$;
DROP TABLE oms_deribit_fix_command_bindings_v1;
""");
}
