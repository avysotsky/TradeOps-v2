-- TO-V2-10. Account-free, opt-in, isolated offline IBKR journal schema.
-- Apply ONLY to a disposable test PostgreSQL database, after review.
-- This is not applied by the TradeOps runtime, migrations, or WSL runner.
-- Requires PostgreSQL 16+.
BEGIN;

CREATE TABLE IF NOT EXISTS ibkr_offline_intents (
    account_key varchar(128) NOT NULL,
    client_order_id varchar(128) NOT NULL,
    symbol varchar(80) NOT NULL,
    side varchar(8) NOT NULL CHECK (side IN ('Buy', 'Sell')),
    requested_quantity numeric(28,12) NOT NULL CHECK (requested_quantity > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_ibkr_offline_intents
        PRIMARY KEY (account_key, client_order_id),
    CONSTRAINT ck_ibkr_offline_intents_nonempty
        CHECK (length(btrim(account_key)) > 0
            AND length(btrim(client_order_id)) > 0
            AND length(btrim(symbol)) > 0)
);

CREATE TABLE IF NOT EXISTS ibkr_offline_events (
    account_key varchar(128) NOT NULL,
    client_order_id varchar(128) NOT NULL,
    event_sequence bigint NOT NULL CHECK (event_sequence > 0),
    event_key varchar(128) NOT NULL CHECK (length(btrim(event_key)) > 0),
    event_type varchar(48) NOT NULL,
    broker_order_id varchar(128) NULL,
    reply_id varchar(128) NULL,
    cumulative_filled numeric(28,12) NULL,
    http_status integer NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_ibkr_offline_events
        PRIMARY KEY (account_key, client_order_id, event_sequence),
    CONSTRAINT uq_ibkr_offline_events_idempotency
        UNIQUE (account_key, client_order_id, event_key),
    CONSTRAINT fk_ibkr_offline_events_intent
        FOREIGN KEY (account_key, client_order_id)
        REFERENCES ibkr_offline_intents (account_key, client_order_id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS ibkr_offline_executions (
    account_key varchar(128) NOT NULL,
    client_order_id varchar(128) NOT NULL,
    execution_id varchar(128) NOT NULL CHECK (length(btrim(execution_id)) > 0),
    broker_order_id varchar(128) NOT NULL CHECK (length(btrim(broker_order_id)) > 0),
    quantity numeric(28,12) NOT NULL CHECK (quantity > 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_ibkr_offline_executions
        PRIMARY KEY (account_key, client_order_id, execution_id),
    CONSTRAINT fk_ibkr_offline_executions_intent
        FOREIGN KEY (account_key, client_order_id)
        REFERENCES ibkr_offline_intents (account_key, client_order_id)
        ON DELETE RESTRICT
);

COMMIT;
