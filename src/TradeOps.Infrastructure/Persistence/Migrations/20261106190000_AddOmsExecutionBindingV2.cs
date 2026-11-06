using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106190000_AddOmsExecutionBindingV2")]
public sealed class AddOmsExecutionBindingV2:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
CREATE TABLE oms_execution_bindings_v2 (
 venue_id varchar(64) NOT NULL,environment smallint NOT NULL,schema_version integer NOT NULL CHECK(schema_version=2),
 node_sha256 varchar(64) NOT NULL CHECK(node_sha256 ~ '^[0-9A-F]{64}$'),lock_root text NOT NULL,
 root_sha256 varchar(64) NOT NULL CHECK(root_sha256 ~ '^[0-9A-F]{64}$'),lock_sha256 varchar(64) NOT NULL CHECK(lock_sha256 ~ '^[0-9A-F]{64}$'),
 actor_role name NOT NULL DEFAULT current_user,recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),PRIMARY KEY(venue_id,environment)
);
CREATE TRIGGER tr_execution_bindings_v2_immutable BEFORE UPDATE OR DELETE ON oms_execution_bindings_v2 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_execution_bindings_v2_no_truncate BEFORE TRUNCATE ON oms_execution_bindings_v2 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
-- v1 metadata is preserved, never silently upgraded into physical authority.
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_execution_bindings_v2) THEN RAISE EXCEPTION 'Preserve physical node binding';END IF;END $$;
DROP TABLE oms_execution_bindings_v2;
""");
}
