-- TO-V2-28 PROPOSAL ONLY. NEVER auto-apply from API, Worker, test or CLI.
-- Owner must approve exact persistent DB identity, tested backup and rollback.
-- Run as controlled migration under a dedicated schema owner; not the importer role.
BEGIN;
CREATE SCHEMA IF NOT EXISTS broker_evidence;

CREATE TABLE broker_evidence.account_bindings (
    binding_id uuid PRIMARY KEY,
    provider varchar(32) NOT NULL CHECK (provider = 'Deribit'),
    environment varchar(16) NOT NULL CHECK (environment = 'mainnet'),
    credential_id_sha256 char(64) NOT NULL
        CHECK (credential_id_sha256 ~ '^[0-9a-f]{64}$'),
    provenance varchar(48) NOT NULL
        CHECK (provenance = 'OwnerApprovedLocalCredentialBinding'),
    owner_approved_at timestamptz NOT NULL,
    -- A credential is not an exchange account ID. Key rotation requires
    -- owner-approved rebinding with independent account-identity verification.
    UNIQUE (provider, environment, credential_id_sha256)
);

CREATE TABLE broker_evidence.position_snapshots (
    snapshot_id uuid PRIMARY KEY,
    binding_id uuid NOT NULL REFERENCES broker_evidence.account_bindings(binding_id)
        ON DELETE RESTRICT,
    request_started_at timestamptz NOT NULL,
    response_received_at timestamptz NOT NULL,
    positions_reported integer NOT NULL CHECK (positions_reported >= 0),
    source_endpoint varchar(64) NOT NULL
        CHECK (source_endpoint = 'private/get_positions?currency=any'),
    CHECK (response_received_at >= request_started_at)
);

CREATE INDEX ix_broker_position_snapshots_account_time
    ON broker_evidence.position_snapshots (binding_id, response_received_at DESC);

CREATE TABLE broker_evidence.position_observations (
    snapshot_id uuid NOT NULL REFERENCES broker_evidence.position_snapshots(snapshot_id)
        ON DELETE RESTRICT,
    instrument_name varchar(160) NOT NULL,
    kind varchar(32) NOT NULL
        CHECK (kind IN ('future','option','future_combo','option_combo')),
    direction varchar(4) NOT NULL CHECK (direction IN ('buy','sell','zero')),
    native_size numeric NOT NULL,
    size_currency numeric NULL,
    PRIMARY KEY (snapshot_id, instrument_name),
    CHECK (length(btrim(instrument_name)) > 0),
    CHECK (direction <> 'zero' OR native_size = 0),
    CHECK (kind <> 'option' OR size_currency IS NULL)
);

-- Reserved for a later separately gated, actual typed-trade importer.
-- Never link to legacy public."Fills" or require a local OrderId.
CREATE TABLE broker_evidence.execution_observations (
    binding_id uuid NOT NULL REFERENCES broker_evidence.account_bindings(binding_id)
        ON DELETE RESTRICT,
    execution_id varchar(160) NOT NULL,
    external_order_id varchar(160) NOT NULL,
    instrument_name varchar(160) NOT NULL,
    direction varchar(4) NOT NULL CHECK (direction IN ('buy','sell')),
    native_amount numeric NOT NULL CHECK (native_amount > 0),
    price numeric NOT NULL CHECK (price > 0),
    signed_fee numeric NOT NULL,
    fee_currency varchar(16) NOT NULL,
    exchange_timestamp_ms bigint NOT NULL CHECK (exchange_timestamp_ms > 0),
    contracts numeric NULL CHECK (contracts IS NULL OR contracts >= 0),
    first_observed_at timestamptz NOT NULL,
    PRIMARY KEY (binding_id, execution_id),
    CHECK (length(btrim(execution_id)) > 0),
    CHECK (length(btrim(external_order_id)) > 0),
    CHECK (length(btrim(instrument_name)) > 0),
    CHECK (length(btrim(fee_currency)) > 0)
);
CREATE INDEX ix_broker_execution_observations_account_time
    ON broker_evidence.execution_observations (binding_id, exchange_timestamp_ms);

CREATE TABLE broker_evidence.trade_query_runs (
    run_id uuid PRIMARY KEY,
    binding_id uuid NOT NULL REFERENCES broker_evidence.account_bindings(binding_id)
        ON DELETE RESTRICT,
    currency varchar(16) NOT NULL,
    window_start_ms bigint NOT NULL,
    window_end_ms bigint NOT NULL,
    historical_index boolean NOT NULL,
    api_pages_exhausted boolean NOT NULL,
    historical_coverage_proven boolean NOT NULL DEFAULT false
        CHECK (historical_coverage_proven = false),
    page_count integer NOT NULL CHECK (page_count BETWEEN 0 AND 32),
    unique_trades integer NOT NULL CHECK (unique_trades BETWEEN 0 AND 10000),
    observed_at timestamptz NOT NULL,
    CHECK (window_start_ms > 0 AND window_end_ms >= window_start_ms),
    CHECK (currency ~ '^[A-Z0-9]{1,16}$')
);
CREATE INDEX ix_broker_trade_query_runs_account_window
    ON broker_evidence.trade_query_runs
    (binding_id, currency, historical_index, window_end_ms DESC);
COMMIT;
