-- TO-V2-19: disposable DATABASE only. Called by the existing real
-- PostgreSQL acceptance script AFTER the API/Worker processes stop.
-- Every fixture write is inside a transaction which is ROLLED BACK.
-- Do not execute against any production/user database.
\set ON_ERROR_STOP on

BEGIN;

DO $to_v2_19$
DECLARE
    order_id uuid;
    extra_order_id uuid;
    client_id text;
    broker_id text;
    symbol_key text;
    recorded_count integer;
    rejected boolean;
BEGIN
    SELECT count(*) INTO recorded_count FROM "Orders";
    IF recorded_count <> 1 THEN
        RAISE EXCEPTION 'TO-V2-19 requires exactly one disposable API order';
    END IF;

    -- No legacy order may be assigned an account/provider automatically.
    SELECT count(*) INTO recorded_count FROM "BrokerOrderIdentities";
    IF recorded_count <> 0 THEN
        RAISE EXCEPTION 'Unexpected scope rows before explicit synthetic binding';
    END IF;

    SELECT "Id", "ClientOrderId", "ExchangeOrderId", "Symbol"
        INTO STRICT order_id, client_id, broker_id, symbol_key
        FROM "Orders";

    IF client_id IS NULL OR symbol_key IS NULL THEN
        RAISE EXCEPTION 'Disposable order has invalid local identity';
    END IF;

    INSERT INTO "BrokerOrderIdentities" (
        "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
        "ClientOrderId", "BrokerOrderId", "Classification", "RecordedAt")
    VALUES (
        order_id, 'Mock', 'to-v2-19-synthetic-account',
        'MOCK:' || symbol_key, client_id, broker_id,
        'SyntheticOnly', now());

    SELECT count(*) INTO recorded_count
    FROM "BrokerOrderIdentities" AS scope
    JOIN "Orders" AS local_order ON scope."OrderId" = local_order."Id"
    WHERE scope."ProviderKey" = 'Mock'
      AND scope."AccountKey" = 'to-v2-19-synthetic-account'
      AND scope."ClientOrderId" = local_order."ClientOrderId"
      AND scope."BrokerOrderId" IS NOT DISTINCT FROM local_order."ExchangeOrderId"
      AND scope."Classification" = 'SyntheticOnly';

    IF recorded_count <> 1 THEN
        RAISE EXCEPTION 'Explicitly scoped synthetic JOIN did not return one row';
    END IF;

    -- No inserts may invent a verified broker identity.
    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            gen_random_uuid(), 'Mock', 'fake', 'MOCK:TEST',
            'fake-client', 'BrokerVerified', now());
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Trusted classification was not prohibited';
    END IF;

    -- Unqualified real IBKR symbols may not masquerade as synthetic conids.
    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            gen_random_uuid(), 'IbkrPaper', 'fake', 'MOCK:AAPL',
            'fake-client', 'SyntheticOnly', now());
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Unsupported provider was accepted';
    END IF;

    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            gen_random_uuid(), 'IbkrOffline', 'fake', 'AAPL',
            'fake-client', 'SyntheticOnly', now());
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Unqualified IBKR instrument was accepted';
    END IF;

    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            gen_random_uuid(), 'Mock', '   ', 'MOCK:BTCUSDT',
            'fake-client', 'SyntheticOnly', now());
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Blank account was accepted';
    END IF;

    -- Positive synthetic record with a nonexistent order must fail FK.
    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            gen_random_uuid(), 'Mock', 'to-v2-19-synthetic-account',
            'MOCK:BTCUSDT', 'orphan-client', 'SyntheticOnly', now());
    EXCEPTION WHEN foreign_key_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Orphan scoped identity was accepted';
    END IF;

    -- Create a disposable second local order WITHIN THIS TRANSACTION ONLY.
    INSERT INTO "Orders" (
        "Id", "ExchangeOrderId", "ClientOrderId", "Symbol", "Side",
        "OrderType", "RequestedQuantity", "FilledQuantity", "AverageFillPrice",
        "Price", "Status", "CreatedAt", "UpdatedAt")
    SELECT gen_random_uuid(), NULL,
        'to-v2-19-synthetic-secondary-' || substring(md5(random()::text), 1, 10),
        "Symbol", "Side", "OrderType", "RequestedQuantity", "FilledQuantity",
        "AverageFillPrice", "Price", "Status", now(), now()
    FROM "Orders" WHERE "Id" = order_id
    RETURNING "Id" INTO STRICT extra_order_id;

    -- New scoped composite index forbids identical account/cOID bindings.
    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            extra_order_id, 'Mock', 'to-v2-19-synthetic-account',
            'MOCK:BTCUSDT', client_id, 'SyntheticOnly', now());
    EXCEPTION WHEN unique_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Provider/account/client uniqueness was not enforced';
    END IF;

    -- A single local OrderId can have only one broker/account scope.
    rejected := false;
    BEGIN
        INSERT INTO "BrokerOrderIdentities" (
            "OrderId", "ProviderKey", "AccountKey", "InstrumentKey",
            "ClientOrderId", "Classification", "RecordedAt")
        VALUES (
            order_id, 'IbkrOffline', 'other-account',
            'SYNTHETIC-IBKR-SYMBOL:BTCUSDT',
            'other-client', 'SyntheticOnly', now());
    EXCEPTION WHEN unique_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'One-to-one OrderId binding was not enforced';
    END IF;
END
$to_v2_19$;

ROLLBACK;

-- These checks execute OUTSIDE the rolled-back transaction.
DO $after_rollback$
BEGIN
    IF (SELECT count(*) FROM "BrokerOrderIdentities") <> 0 THEN
        RAISE EXCEPTION 'Synthetic identity fixture leaked after rollback';
    END IF;

    IF (SELECT count(*) FROM "Orders") <> 1 THEN
        RAISE EXCEPTION 'Legacy order count changed after rollback';
    END IF;

    IF (SELECT count(*) FROM "__EFMigrationsHistory"
        WHERE "MigrationId" = '20261022235900_AddSyntheticBrokerOrderIdentities') <> 1 THEN
        RAISE EXCEPTION 'Broker identity EF migration was not recorded exactly once';
    END IF;

    IF (SELECT count(*) FROM pg_indexes
        WHERE schemaname = 'public'
          AND tablename = 'BrokerOrderIdentities'
          AND indexname LIKE 'UX_BrokerOrderIdentities_ProviderAccount%') <> 2 THEN
        RAISE EXCEPTION 'Expected two scoped unique indexes';
    END IF;
END
$after_rollback$;

SELECT 'TRADEOPS REAL BROKER SCOPE SQL PASS synthetic_only=true legacy_unscoped=true constraints=true rollback=true';
