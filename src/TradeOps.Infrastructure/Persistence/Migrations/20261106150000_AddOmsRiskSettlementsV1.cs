using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106150000_AddOmsRiskSettlementsV1")]
public sealed class AddOmsRiskSettlementsV1 : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
CREATE TABLE oms_risk_settlements_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL, command_id uuid NOT NULL,
 run_id uuid NOT NULL, generation bigint NOT NULL CHECK(generation>0), schema_version integer NOT NULL CHECK(schema_version=1),
 state_sha256 bytea NOT NULL CHECK(octet_length(state_sha256)=32),
 actor_role name NOT NULL DEFAULT current_user, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
 REFERENCES oms_risk_reservations_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
 REFERENCES oms_recovered_account_states_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TRIGGER tr_risk_settlements_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_settlements_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_settlements_v1_no_truncate BEFORE TRUNCATE ON oms_risk_settlements_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");
    protected override void Down(MigrationBuilder b) => b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_risk_settlements_v1) THEN RAISE EXCEPTION 'Preserve risk settlement audit'; END IF; END $$;
DROP TABLE oms_risk_settlements_v1;
""");
}
