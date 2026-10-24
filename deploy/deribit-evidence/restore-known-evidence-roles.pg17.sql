-- TO-V2-35: portable, non-secret role bootstrap for an empty evidence
-- recovery on a fresh dedicated PostgreSQL 17. NEVER apply to the live DB.
-- This defines only our two known NOLOGIN roles; no bootstrap password,
-- credential fingerprints, exchange records, or other project's roles.
BEGIN;
DO $guard$
BEGIN
  IF current_database() <> 'postgres'
     OR current_setting('server_version_num')::integer < 170000
     OR current_setting('server_version_num')::integer >= 180000
     OR EXISTS (SELECT 1 FROM pg_roles
                WHERE rolname IN ('tradeops_evidence_owner',
                                  'tradeops_evidence_writer'))
     OR EXISTS (SELECT 1 FROM pg_database
                WHERE datname = 'tradeops_evidence')
  THEN
    RAISE EXCEPTION 'NOT_FRESH_PG17_RECOVERY_TARGET';
  END IF;
END $guard$;

CREATE ROLE tradeops_evidence_owner
  NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE tradeops_evidence_writer
  NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
  INHERIT CONNECTION LIMIT 2;
ALTER ROLE tradeops_evidence_writer SET statement_timeout = '15s';
ALTER ROLE tradeops_evidence_writer SET lock_timeout = '3s';
ALTER ROLE tradeops_evidence_writer
  SET idle_in_transaction_session_timeout = '10s';
ALTER ROLE tradeops_evidence_writer SET search_path = pg_catalog;
COMMIT;
