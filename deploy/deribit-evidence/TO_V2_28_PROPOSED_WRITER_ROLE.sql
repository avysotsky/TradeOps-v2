-- TO-V2-28: OWNER-GATED manual role setup for a newly isolated database.
-- Do NOT run until the dedicated PostgreSQL 17 database identity and
-- evidence schema have been verified, migration approved and backed up.
-- Run only in the dedicated 'tradeops_evidence' database using a
-- privileged operator session, never in JobRadar / external-agent DBs.
-- This script intentionally DOES NOT SET PASSWORDS and DOES NOT
-- authorize any Deribit data migration or broker request.
BEGIN;

DO $$
BEGIN
    IF current_database() <> 'tradeops_evidence'
        OR current_setting('server_version_num')::int < 170000
        OR current_setting('server_version_num')::int >= 180000
        OR to_regnamespace('broker_evidence') IS NULL THEN
        RAISE EXCEPTION 'Unapproved DB, unsupported major version or missing evidence schema';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tradeops_evidence_writer') THEN
        RAISE EXCEPTION 'Evidence writer already exists: manual review required';
    END IF;
END $$;

CREATE ROLE tradeops_evidence_writer LOGIN
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
    CONNECTION LIMIT 3;

REVOKE ALL ON DATABASE tradeops_evidence FROM PUBLIC;
GRANT CONNECT ON DATABASE tradeops_evidence TO tradeops_evidence_writer;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA broker_evidence TO tradeops_evidence_writer;

-- Only the exact metadata read used by the app. No account binding edits.
GRANT SELECT ON TABLE broker_evidence.account_bindings
    TO tradeops_evidence_writer;
-- Only additive position writes. No UPDATE/DELETE/DDL or execution writes.
GRANT INSERT ON TABLE
    broker_evidence.position_snapshots,
    broker_evidence.position_observations
    TO tradeops_evidence_writer;

ALTER ROLE tradeops_evidence_writer SET statement_timeout = '15s';
ALTER ROLE tradeops_evidence_writer SET lock_timeout = '3s';
ALTER ROLE tradeops_evidence_writer SET idle_in_transaction_session_timeout = '10s';

COMMIT;

-- Separate manual interactive step in psql AFTER successful commit:
--   \password tradeops_evidence_writer
-- Never put the password in a committed SQL script or shell history.
