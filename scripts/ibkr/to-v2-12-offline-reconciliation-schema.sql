-- TO-V2-12: isolated opt-in companion to TO-V2-10 offline journal.
-- For a disposable, local PostgreSQL 16+ database / private smoke schema ONLY.
-- Apply TO-V2-10 tables first, then this script. No production migration.
BEGIN;

CREATE TABLE ibkr_offline_reconciliation_observations (
    account_key varchar(128) NOT NULL,
    client_order_id varchar(128) NOT NULL,
    observation_sequence bigint NOT NULL CHECK (observation_sequence > 0),
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    evidence_json text NOT NULL CHECK (length(evidence_json) BETWEEN 2 AND 262144),
    disposition varchar(32) NOT NULL CHECK (disposition IN
        ('Inconclusive', 'NotFound', 'Working', 'PartiallyFilled', 'Filled', 'Cancelled')),
    lifecycle_event_sequence bigint NULL CHECK (lifecycle_event_sequence > 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_ibkr_offline_reconciliation_observations
        PRIMARY KEY (account_key, client_order_id, observation_sequence),
    CONSTRAINT fk_ibkr_offline_reconciliation_intent
        FOREIGN KEY (account_key, client_order_id)
        REFERENCES ibkr_offline_intents (account_key, client_order_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_ibkr_offline_reconciliation_event
        FOREIGN KEY (account_key, client_order_id, lifecycle_event_sequence)
        REFERENCES ibkr_offline_events (account_key, client_order_id, event_sequence)
        ON DELETE RESTRICT,
    CONSTRAINT ck_ibkr_offline_reconciliation_event_required
        CHECK ((disposition IN ('Inconclusive','NotFound')
                AND lifecycle_event_sequence IS NULL)
            OR (disposition IN ('Working','PartiallyFilled','Filled','Cancelled')
                AND lifecycle_event_sequence IS NOT NULL))
);

COMMIT;
