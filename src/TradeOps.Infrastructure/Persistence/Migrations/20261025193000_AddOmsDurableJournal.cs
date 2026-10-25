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
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE oms_account_leases;
DROP TABLE oms_integrity_incidents;
DROP TABLE oms_inbox;
DROP TABLE oms_attempts;
DROP TABLE oms_outbox;
DROP TABLE oms_commands;
DROP TABLE oms_intents;
DROP TABLE oms_aggregates;
""");
    }
}
