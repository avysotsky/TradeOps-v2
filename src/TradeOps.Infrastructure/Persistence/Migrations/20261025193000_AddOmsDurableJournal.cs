using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

/// <summary>
/// Independent, additive B2 journal schema. No legacy Orders/Fills keys are changed.
/// Raw SQL deliberately keeps this store out of the legacy EF tracked model.
/// </summary>
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261025193000_AddOmsDurableJournal")]
public sealed class AddOmsDurableJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE oms_aggregates (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL CHECK (environment BETWEEN 1 AND 4),
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL DEFAULT '',
 aggregate_id uuid NOT NULL,
 version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_aggregates PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
);

CREATE TABLE oms_intents (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 intent_id uuid NOT NULL,
 aggregate_id uuid NOT NULL,
 schema_version int NOT NULL CHECK (schema_version > 0),
 provenance varchar(128) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_intents PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,intent_id),
 CONSTRAINT fk_oms_intents_aggregate FOREIGN KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
   REFERENCES oms_aggregates(tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
);

CREATE TABLE oms_commands (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL,
 intent_id uuid NOT NULL,
 aggregate_id uuid NOT NULL,
 expected_version bigint NOT NULL CHECK (expected_version >= 0),
 aggregate_version bigint NOT NULL CHECK (aggregate_version > 0),
 schema_version int NOT NULL CHECK (schema_version > 0),
 command_kind varchar(16) NOT NULL CHECK (command_kind IN ('Place','Cancel','Replace')),
 payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256)=32),
 request_json jsonb NOT NULL,
 dispatch_phase varchar(32) NOT NULL DEFAULT 'PersistedNotSent'
   CHECK (dispatch_phase IN ('PersistedNotSent','DispatchStarted','Acknowledged','Rejected','Ambiguous')),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_commands PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 CONSTRAINT fk_oms_commands_intent FOREIGN KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,intent_id)
   REFERENCES oms_intents(tenant_id,venue_id,environment,account_id,subaccount_id,intent_id),
 CONSTRAINT fk_oms_commands_aggregate FOREIGN KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
   REFERENCES oms_aggregates(tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id),
 CONSTRAINT uq_oms_command_aggregate_version UNIQUE
   (tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id,aggregate_version)
);

CREATE TABLE oms_outbox (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL,
 state varchar(24) NOT NULL DEFAULT 'NotSent'
   CHECK (state IN ('NotSent','NeedsRecovery','Resolved')),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 changed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_outbox PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 CONSTRAINT fk_oms_outbox_command FOREIGN KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
   REFERENCES oms_commands(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);
CREATE INDEX ix_oms_outbox_state ON oms_outbox(state, created_at);

CREATE TABLE oms_attempts (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 command_id uuid NOT NULL,
 attempt_number integer NOT NULL CHECK (attempt_number > 0),
 dispatch_phase varchar(32) NOT NULL
   CHECK (dispatch_phase IN ('DispatchStarted','Acknowledged','Rejected','Ambiguous')),
 started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_attempts PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,command_id,attempt_number),
 CONSTRAINT fk_oms_attempts_command FOREIGN KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
   REFERENCES oms_commands(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);

CREATE TABLE oms_inbox (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 event_namespace varchar(160) NOT NULL,
 event_id varchar(160) NOT NULL,
 event_kind varchar(16) NOT NULL CHECK (event_kind IN ('Order','Execution')),
 aggregate_id uuid NULL,
 schema_version int NOT NULL CHECK (schema_version > 0),
 payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256)=32),
 event_json jsonb NOT NULL,
 received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT pk_oms_inbox PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id,event_namespace,event_id)
);
CREATE INDEX ix_oms_inbox_received ON oms_inbox(received_at);

CREATE TABLE oms_integrity_incidents (
 incident_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 kind varchar(32) NOT NULL CHECK (kind IN ('CommandCollision','EventCollision','IntentCollision')),
 correlation varchar(160) NOT NULL,
 observed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE oms_account_leases (
 tenant_id varchar(128) NOT NULL,
 venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL,
 account_id varchar(128) NOT NULL,
 subaccount_id varchar(128) NOT NULL,
 generation bigint NOT NULL CHECK (generation > 0),
 owner_id uuid NOT NULL,
 expires_at timestamptz NOT NULL,
 CONSTRAINT pk_oms_account_leases PRIMARY KEY
   (tenant_id,venue_id,environment,account_id,subaccount_id)
);

-- Ledger rows are immutable for the application database role.
CREATE FUNCTION oms_deny_row_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'OMS journal evidence is append-only';
END; $$;
CREATE TRIGGER tr_oms_intents_immutable BEFORE UPDATE OR DELETE ON oms_intents
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_oms_inbox_immutable BEFORE UPDATE OR DELETE ON oms_inbox
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_oms_incidents_immutable BEFORE UPDATE OR DELETE ON oms_integrity_incidents
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();

CREATE FUNCTION oms_guard_command_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'OMS command cannot be deleted'; END IF;
 IF ROW(NEW.tenant_id,NEW.venue_id,NEW.environment,NEW.account_id,NEW.subaccount_id,
        NEW.command_id,NEW.intent_id,NEW.aggregate_id,NEW.expected_version,
        NEW.aggregate_version,NEW.schema_version,NEW.command_kind,
        NEW.payload_sha256,NEW.request_json,NEW.created_at)
    IS DISTINCT FROM
    ROW(OLD.tenant_id,OLD.venue_id,OLD.environment,OLD.account_id,OLD.subaccount_id,
        OLD.command_id,OLD.intent_id,OLD.aggregate_id,OLD.expected_version,
        OLD.aggregate_version,OLD.schema_version,OLD.command_kind,
        OLD.payload_sha256,OLD.request_json,OLD.created_at)
 THEN RAISE EXCEPTION 'OMS immutable command payload mutation'; END IF;
 IF NOT (
    NEW.dispatch_phase = OLD.dispatch_phase OR
    (OLD.dispatch_phase='PersistedNotSent' AND NEW.dispatch_phase='DispatchStarted') OR
    (OLD.dispatch_phase IN ('DispatchStarted','Ambiguous') AND
     NEW.dispatch_phase IN ('Acknowledged','Rejected','Ambiguous'))
 ) THEN RAISE EXCEPTION 'OMS invalid dispatch phase regression'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER tr_oms_commands_guard BEFORE UPDATE OR DELETE ON oms_commands
 FOR EACH ROW EXECUTE FUNCTION oms_guard_command_mutation();

CREATE FUNCTION oms_guard_outbox_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'OMS outbox cannot be deleted'; END IF;
 IF ROW(NEW.tenant_id,NEW.venue_id,NEW.environment,NEW.account_id,NEW.subaccount_id,
        NEW.command_id,NEW.created_at) IS DISTINCT FROM
    ROW(OLD.tenant_id,OLD.venue_id,OLD.environment,OLD.account_id,OLD.subaccount_id,
        OLD.command_id,OLD.created_at)
 THEN RAISE EXCEPTION 'OMS outbox identity mutation'; END IF;
 IF NOT (NEW.state = OLD.state OR
        (OLD.state='NotSent' AND NEW.state='NeedsRecovery') OR
        (OLD.state='NeedsRecovery' AND NEW.state='Resolved'))
 THEN RAISE EXCEPTION 'OMS outbox cannot be rearmed'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER tr_oms_outbox_guard BEFORE UPDATE OR DELETE ON oms_outbox
 FOR EACH ROW EXECUTE FUNCTION oms_guard_outbox_mutation();

CREATE FUNCTION oms_guard_attempt_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'OMS attempt cannot be deleted'; END IF;
 IF ROW(NEW.tenant_id,NEW.venue_id,NEW.environment,NEW.account_id,NEW.subaccount_id,
        NEW.command_id,NEW.attempt_number,NEW.started_at) IS DISTINCT FROM
    ROW(OLD.tenant_id,OLD.venue_id,OLD.environment,OLD.account_id,OLD.subaccount_id,
        OLD.command_id,OLD.attempt_number,OLD.started_at)
 THEN RAISE EXCEPTION 'OMS attempt identity mutation'; END IF;
 IF NOT (NEW.dispatch_phase = OLD.dispatch_phase OR
        (OLD.dispatch_phase='DispatchStarted' AND
         NEW.dispatch_phase IN ('Acknowledged','Rejected','Ambiguous')) OR
        (OLD.dispatch_phase='Ambiguous' AND
         NEW.dispatch_phase IN ('Acknowledged','Rejected')))
 THEN RAISE EXCEPTION 'OMS attempt phase regression'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER tr_oms_attempts_guard BEFORE UPDATE OR DELETE ON oms_attempts
 FOR EACH ROW EXECUTE FUNCTION oms_guard_attempt_mutation();

CREATE FUNCTION oms_guard_aggregate_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'OMS aggregate cannot be deleted'; END IF;
 IF ROW(NEW.tenant_id,NEW.venue_id,NEW.environment,NEW.account_id,NEW.subaccount_id,
        NEW.aggregate_id,NEW.created_at) IS DISTINCT FROM
    ROW(OLD.tenant_id,OLD.venue_id,OLD.environment,OLD.account_id,OLD.subaccount_id,
        OLD.aggregate_id,OLD.created_at)
 THEN RAISE EXCEPTION 'OMS aggregate identity mutation'; END IF;
 IF NEW.version <> OLD.version+1 THEN
  RAISE EXCEPTION 'OMS aggregate version must increment exactly once'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER tr_oms_aggregates_guard BEFORE UPDATE OR DELETE ON oms_aggregates
 FOR EACH ROW EXECUTE FUNCTION oms_guard_aggregate_mutation();

""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM oms_aggregates)
    OR EXISTS (SELECT 1 FROM oms_intents)
    OR EXISTS (SELECT 1 FROM oms_commands)
    OR EXISTS (SELECT 1 FROM oms_outbox)
    OR EXISTS (SELECT 1 FROM oms_attempts)
    OR EXISTS (SELECT 1 FROM oms_inbox)
    OR EXISTS (SELECT 1 FROM oms_integrity_incidents)
    OR EXISTS (SELECT 1 FROM oms_account_leases)
 THEN RAISE EXCEPTION 'Refusing destructive OMS journal rollback: export and verify empty tables first';
 END IF;
END; $$;
DROP TABLE oms_account_leases;
DROP TABLE oms_integrity_incidents;
DROP TABLE oms_inbox;
DROP TABLE oms_attempts;
DROP TABLE oms_outbox;
DROP TABLE oms_commands;
DROP TABLE oms_intents;
DROP TABLE oms_aggregates;
DROP FUNCTION oms_guard_aggregate_mutation();
DROP FUNCTION oms_guard_attempt_mutation();
DROP FUNCTION oms_guard_outbox_mutation();
DROP FUNCTION oms_guard_command_mutation();
DROP FUNCTION oms_deny_row_mutation();
""");
    }
}
