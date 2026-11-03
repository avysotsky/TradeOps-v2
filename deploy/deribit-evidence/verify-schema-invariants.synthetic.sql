-- TO-V2-30: synthetic-only SQL fixtures, executed strictly in the
-- separate ephemeral, unnetworked PostgreSQL17 rehearsal container.
-- NO real Deribit account IDs or actual exchange records.
BEGIN;
INSERT INTO broker_evidence.account_bindings
  (binding_id,provider,environment,credential_id_sha256,provenance,owner_approved_at)
VALUES
  ('00000000-0000-4000-8000-000000000001','Deribit','mainnet',repeat('a',64),'OwnerApprovedLocalCredentialBinding',now()),
  ('00000000-0000-4000-8000-000000000002','Deribit','mainnet',repeat('b',64),'OwnerApprovedLocalCredentialBinding',now());
INSERT INTO broker_evidence.position_snapshots
  (snapshot_id,binding_id,request_started_at,response_received_at,positions_reported,source_endpoint)
VALUES
  ('00000000-0000-4000-8000-000000000003',
   '00000000-0000-4000-8000-000000000001',now(),now(),1,'private/get_positions?currency=any');
INSERT INTO broker_evidence.position_observations
  (snapshot_id,instrument_name,kind,direction,native_size,size_currency)
VALUES
  ('00000000-0000-4000-8000-000000000003','BTC-PERPETUAL','future','sell',-100,0.002);

INSERT INTO broker_evidence.execution_observations
  (binding_id,execution_id,external_order_id,instrument_name,direction,
   native_amount,price,signed_fee,fee_currency,exchange_timestamp_ms,
   contracts,first_observed_at)
VALUES
  ('00000000-0000-4000-8000-000000000001','synthetic-exec-1','order-A',
   'BTC-PERPETUAL','buy',100,65000,-0.00001,'BTC',1791000000000,NULL,now()),
  ('00000000-0000-4000-8000-000000000002','synthetic-exec-1','order-B',
   'BTC-PERPETUAL','sell',200,65000,0.00002,'BTC',1791000000000,NULL,now());

DO $$
DECLARE n integer;
BEGIN
  -- A repeated ID in one binding is prohibited by the primary key,
  -- regardless of purported order ID or execution amount.
  BEGIN
    INSERT INTO broker_evidence.execution_observations
      (binding_id,execution_id,external_order_id,instrument_name,direction,
       native_amount,price,signed_fee,fee_currency,exchange_timestamp_ms,
       contracts,first_observed_at)
    VALUES
      ('00000000-0000-4000-8000-000000000001','synthetic-exec-1','conflict-order',
       'BTC-PERPETUAL','buy',999,65000,0,'BTC',1791000000000,NULL,now());
    RAISE EXCEPTION 'same-account duplicate unexpectedly allowed';
  EXCEPTION WHEN unique_violation THEN
    NULL;
  END;
  SELECT count(*) INTO n FROM broker_evidence.execution_observations
    WHERE execution_id='synthetic-exec-1';
  IF n <> 2 THEN RAISE EXCEPTION 'account scoping error'; END IF;
  IF NOT EXISTS (
    SELECT 1 FROM broker_evidence.execution_observations
     WHERE binding_id='00000000-0000-4000-8000-000000000001'
       AND external_order_id='order-A' AND signed_fee=-0.00001
       AND native_amount=100
  ) THEN RAISE EXCEPTION 'immutable evidence changed'; END IF;
  IF NOT EXISTS (
    SELECT 1 FROM broker_evidence.position_observations
      WHERE instrument_name='BTC-PERPETUAL' AND native_size=-100
        AND size_currency=0.002 AND direction='sell'
  ) THEN RAISE EXCEPTION 'native signed size changed'; END IF;
END $$;
ROLLBACK;
