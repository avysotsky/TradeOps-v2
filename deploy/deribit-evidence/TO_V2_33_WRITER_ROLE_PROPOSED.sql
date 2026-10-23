-- TO-V2-33 PROPOSAL ONLY. Owner must explicitly authorize separately
-- before applying to persistent tradeops_evidence. This script is rehearsed
-- exclusively in a temporary network-disabled PG17 in this workstream.
BEGIN;

DO $guard$
DECLARE
    tables_owned integer;
    records integer;
BEGIN
    IF current_database() <> 'tradeops_evidence'
       OR current_setting('server_version_num')::integer < 170000
       OR current_setting('server_version_num')::integer >= 180000
       OR to_regnamespace('broker_evidence') IS NULL
       OR NOT EXISTS (SELECT 1 FROM pg_roles
                      WHERE rolname = 'tradeops_evidence_owner'
                        AND NOT rolcanlogin)
       OR EXISTS (SELECT 1 FROM pg_roles
                  WHERE rolname = 'tradeops_evidence_writer')
    THEN
        RAISE EXCEPTION 'WRITER_PRECONDITION_FAILED';
    END IF;

    SELECT count(*) INTO tables_owned
    FROM pg_class c
    JOIN pg_namespace n ON c.relnamespace = n.oid
    JOIN pg_roles r ON r.oid = c.relowner
    WHERE n.nspname = 'broker_evidence' AND c.relkind = 'r'
      AND r.rolname = 'tradeops_evidence_owner';

    IF tables_owned <> 5 THEN
        RAISE EXCEPTION 'SCHEMA_OWNER_OR_TABLE_COUNT_MISMATCH';
    END IF;

    SELECT
        (SELECT count(*) FROM broker_evidence.account_bindings)
      + (SELECT count(*) FROM broker_evidence.position_snapshots)
      + (SELECT count(*) FROM broker_evidence.position_observations)
      + (SELECT count(*) FROM broker_evidence.execution_observations)
      + (SELECT count(*) FROM broker_evidence.trade_query_runs)
    INTO records;

    IF records <> 0 THEN
        RAISE EXCEPTION 'WRITER_PROVISIONING_ONLY_ALLOWED_WHILE_EMPTY';
    END IF;
END $guard$;

-- Deliberately NOLOGIN. Creating a database role does NOT activate capture.
-- A later separately approved, owner-local interactive credential ceremony
-- must establish LOGIN and credentials without secrets in Git/terminal logs.
CREATE ROLE tradeops_evidence_writer
    NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
    INHERIT CONNECTION LIMIT 2;

-- Do not alter PUBLIC privileges or other project databases.
GRANT CONNECT ON DATABASE tradeops_evidence TO tradeops_evidence_writer;
GRANT USAGE ON SCHEMA broker_evidence TO tradeops_evidence_writer;
GRANT SELECT ON TABLE broker_evidence.account_bindings
    TO tradeops_evidence_writer;
GRANT INSERT ON TABLE
    broker_evidence.position_snapshots,
    broker_evidence.position_observations
    TO tradeops_evidence_writer;

ALTER ROLE tradeops_evidence_writer SET statement_timeout = '15s';
ALTER ROLE tradeops_evidence_writer SET lock_timeout = '3s';
ALTER ROLE tradeops_evidence_writer
    SET idle_in_transaction_session_timeout = '10s';
ALTER ROLE tradeops_evidence_writer SET search_path = pg_catalog;

DO $rights$
DECLARE
    login_enabled boolean;
BEGIN
    SELECT rolcanlogin INTO login_enabled
    FROM pg_roles WHERE rolname = 'tradeops_evidence_writer';
    IF login_enabled IS DISTINCT FROM false
       OR NOT has_database_privilege('tradeops_evidence_writer',
                                      'tradeops_evidence', 'CONNECT')
       OR has_database_privilege('tradeops_evidence_writer',
                                 'tradeops_evidence', 'CREATE')
       OR has_schema_privilege('tradeops_evidence_writer',
                                'broker_evidence', 'CREATE')
       OR has_schema_privilege('tradeops_evidence_writer',
                                'public', 'CREATE')
       OR NOT has_table_privilege('tradeops_evidence_writer',
                                  'broker_evidence.account_bindings', 'SELECT')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.account_bindings', 'INSERT')
       OR NOT has_table_privilege('tradeops_evidence_writer',
                                  'broker_evidence.position_snapshots', 'INSERT')
       OR NOT has_table_privilege('tradeops_evidence_writer',
                                  'broker_evidence.position_observations', 'INSERT')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.position_snapshots', 'SELECT')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.position_snapshots', 'UPDATE')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.position_snapshots', 'DELETE')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.execution_observations', 'INSERT')
       OR has_table_privilege('tradeops_evidence_writer',
                              'broker_evidence.trade_query_runs', 'INSERT')
    THEN
        RAISE EXCEPTION 'WRITER_LEAST_PRIVILEGE_CHECK_FAILED';
    END IF;
END $rights$;

COMMIT;
