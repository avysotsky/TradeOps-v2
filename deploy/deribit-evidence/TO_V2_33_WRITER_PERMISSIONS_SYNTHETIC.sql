-- TO-V2-33 synthetic fixtures for disposable PostgreSQL 17 only.
-- None of these GUIDs, IDs, instrument prices or execution evidence is real.
BEGIN;

INSERT INTO broker_evidence.account_bindings
    (binding_id, provider, environment, credential_id_sha256,
     provenance, owner_approved_at)
VALUES
    ('00000000-0000-4000-8000-000000000101',
     'Deribit','mainnet',repeat('a', 64),
     'OwnerApprovedLocalCredentialBinding',now());

SET ROLE tradeops_evidence_writer;

-- Binding metadata SELECT permitted.
DO $test$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM broker_evidence.account_bindings
        WHERE binding_id = '00000000-0000-4000-8000-000000000101'
          AND provider = 'Deribit'
          AND environment = 'mainnet'
    ) THEN
        RAISE EXCEPTION 'WRITER_BINDING_SELECT_FAILED';
    END IF;
END $test$;

-- Exactly these two evidence tables accept INSERT.
INSERT INTO broker_evidence.position_snapshots
    (snapshot_id, binding_id, request_started_at, response_received_at,
     positions_reported, source_endpoint)
VALUES
    ('00000000-0000-4000-8000-000000000102',
     '00000000-0000-4000-8000-000000000101',
     now(),now(),1,'private/get_positions?currency=any');

INSERT INTO broker_evidence.position_observations
    (snapshot_id, instrument_name, kind, direction, native_size, size_currency)
VALUES
    ('00000000-0000-4000-8000-000000000102',
     'BTC-PERPETUAL','future','sell',-100,0.002);

-- UPDATE, DELETE, CREATE TABLE, INSERT bindings/executions must fail.
DO $test$
BEGIN
    BEGIN
        EXECUTE 'UPDATE broker_evidence.position_snapshots SET positions_reported=0';
        RAISE EXCEPTION 'UNEXPECTED_UPDATE_PERMISSION';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        EXECUTE 'DELETE FROM broker_evidence.position_observations';
        RAISE EXCEPTION 'UNEXPECTED_DELETE_PERMISSION';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        EXECUTE 'CREATE TABLE broker_evidence.invalid_writer_ddl (id int)';
        RAISE EXCEPTION 'UNEXPECTED_CREATE_PERMISSION';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        EXECUTE 'INSERT INTO broker_evidence.account_bindings
                   (binding_id,provider,environment,credential_id_sha256,
                    provenance,owner_approved_at)
                 VALUES
                   (''00000000-0000-4000-8000-000000000103'',
                    ''Deribit'',''mainnet'',repeat(''b'',64),
                    ''OwnerApprovedLocalCredentialBinding'',now())';
        RAISE EXCEPTION 'UNEXPECTED_BINDING_INSERT_PERMISSION';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    BEGIN
        EXECUTE 'INSERT INTO broker_evidence.execution_observations
                    (binding_id,execution_id,external_order_id,
                     instrument_name,direction,native_amount,price,
                     signed_fee,fee_currency,exchange_timestamp_ms,
                     first_observed_at)
                 VALUES
                   (''00000000-0000-4000-8000-000000000101'',
                    ''not-allowed'',''not-allowed'',''BTC-PERPETUAL'',''buy'',
                    100,65000,0,''BTC'',1791000000000,now())';
        RAISE EXCEPTION 'UNEXPECTED_EXECUTION_INSERT_PERMISSION';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $test$;

RESET ROLE;

DO $test$
DECLARE
    n integer;
BEGIN
    SELECT count(*) INTO n FROM broker_evidence.position_observations
    WHERE snapshot_id = '00000000-0000-4000-8000-000000000102'
      AND native_size=-100 AND size_currency=0.002;
    IF n <> 1 THEN
        RAISE EXCEPTION 'WRITER_POSITION_INSERT_ROUNDTRIP_FAILED';
    END IF;
    IF (SELECT count(*) FROM broker_evidence.account_bindings) <> 1
       OR (SELECT count(*) FROM broker_evidence.execution_observations) <> 0
    THEN
        RAISE EXCEPTION 'WRITER_RESTRICTED_ROWS_UNEXPECTED';
    END IF;
END $test$;

ROLLBACK;
