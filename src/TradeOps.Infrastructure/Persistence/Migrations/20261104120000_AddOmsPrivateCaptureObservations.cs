using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261104120000_AddOmsPrivateCaptureObservations")]
public sealed class AddOmsPrivateCaptureObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Existing inbox v1 rows/encoding are untouched. New facts use explicit schema v2 namespaces.
-- Observations also retain conflicting incoming facts, so there is deliberately no FK
-- requiring every quarantined observation to become an accepted inbox event.
CREATE TABLE oms_private_capture_observations (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL CHECK (venue_id='DERIBIT'),
 environment smallint NOT NULL CHECK (environment BETWEEN 1 AND 4),
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL DEFAULT '',
 capture_id uuid NOT NULL,
 observation_sha256 bytea NOT NULL CHECK (octet_length(observation_sha256)=32),
 schema_version integer NOT NULL CHECK (schema_version=1),
 event_namespace varchar(160) NOT NULL,
 event_id varchar(160) NOT NULL,
 stream_epoch uuid NOT NULL,
 disposition smallint NOT NULL CHECK (disposition IN (1,2,3)),
 observation_json jsonb NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_private_capture_observations PRIMARY KEY
  (tenant_id,venue_id,environment,account_id,subaccount_id,capture_id,observation_sha256)
);
CREATE INDEX ix_oms_private_capture_observations_event ON oms_private_capture_observations
 (tenant_id,venue_id,environment,account_id,subaccount_id,event_namespace,event_id,recorded_at);
CREATE TRIGGER tr_oms_private_capture_observations_immutable
 BEFORE UPDATE OR DELETE ON oms_private_capture_observations
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_oms_private_capture_observations_no_truncate
 BEFORE TRUNCATE ON oms_private_capture_observations
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_private_capture_observations)
    OR EXISTS (SELECT 1 FROM oms_inbox WHERE schema_version=2)
 THEN RAISE EXCEPTION 'Refusing destructive private capture rollback: preserve v2 facts and observations';
 END IF;
END; $$;
DROP TABLE oms_private_capture_observations;
""");
}
