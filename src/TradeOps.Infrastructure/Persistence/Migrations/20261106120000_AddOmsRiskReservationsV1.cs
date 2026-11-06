using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106120000_AddOmsRiskReservationsV1")]
public sealed class AddOmsRiskReservationsV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE oms_risk_reservations_v1 (
 reservation_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL, run_id uuid NOT NULL, generation bigint NOT NULL CHECK(generation>0),
 schema_version integer NOT NULL CHECK(schema_version=1),
 command_sha256 bytea NOT NULL CHECK(octet_length(command_sha256)=32),
 state_sha256 bytea NOT NULL CHECK(octet_length(state_sha256)=32),
 evidence_before bigint NOT NULL CHECK(evidence_before>=0),
 evidence_after bigint NOT NULL CHECK(evidence_after>evidence_before),
 reserved_usd numeric NOT NULL CHECK(reserved_usd>0), margin_usd numeric NOT NULL CHECK(margin_usd>=0),
 inputs_json jsonb NOT NULL, inputs_sha256 bytea NOT NULL CHECK(octet_length(inputs_sha256)=32),
 actor_role name NOT NULL DEFAULT current_user, recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
 REFERENCES oms_commands(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);
CREATE INDEX ix_oms_risk_reservations_v1_scope ON oms_risk_reservations_v1
 (tenant_id,venue_id,environment,account_id,subaccount_id,reservation_id DESC);
CREATE TRIGGER tr_risk_reservations_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_reservations_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_reservations_v1_no_truncate BEFORE TRUNCATE ON oms_risk_reservations_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM oms_risk_reservations_v1) THEN
 RAISE EXCEPTION 'Refusing destructive risk rollback: preserve pending exposure'; END IF;
END $$;
DROP TABLE oms_risk_reservations_v1;
""");
}
