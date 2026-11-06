using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106200000_AddOmsFixSessionStoreV1")]
public sealed class AddOmsFixSessionStoreV1:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
CREATE TABLE oms_fix_sessions_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 sender_comp_id varchar(64) NOT NULL,target_comp_id varchar(64) NOT NULL CHECK(target_comp_id='DERIBITSERVER'),schema_version integer NOT NULL CHECK(schema_version=1),
 key_sha256 bytea NOT NULL CHECK(octet_length(key_sha256)=32),created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 next_sender bigint NOT NULL DEFAULT 1 CHECK(next_sender BETWEEN 1 AND 1000000000),next_target bigint NOT NULL DEFAULT 1 CHECK(next_target BETWEEN 1 AND 1000000000),
 last_logon_ms bigint NOT NULL DEFAULT 0 CHECK(last_logon_ms>=0),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
);
CREATE FUNCTION public.oms_guard_fix_session_update_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (NEW.tenant_id,NEW.venue_id,NEW.environment,NEW.account_id,NEW.subaccount_id,NEW.sender_comp_id,NEW.target_comp_id,NEW.schema_version,NEW.key_sha256,NEW.created_at)
 IS DISTINCT FROM (OLD.tenant_id,OLD.venue_id,OLD.environment,OLD.account_id,OLD.subaccount_id,OLD.sender_comp_id,OLD.target_comp_id,OLD.schema_version,OLD.key_sha256,OLD.created_at)
 OR NEW.next_sender<OLD.next_sender OR NEW.next_sender>OLD.next_sender+1 OR NEW.next_target<OLD.next_target OR NEW.next_target>OLD.next_target+10000 OR NEW.last_logon_ms<OLD.last_logon_ms
 THEN RAISE EXCEPTION 'Immutable FIX identity or regressing sequence'; END IF; RETURN NEW;
END $$;
CREATE TRIGGER tr_fix_sessions_v1_identity BEFORE UPDATE ON oms_fix_sessions_v1 FOR EACH ROW EXECUTE FUNCTION public.oms_guard_fix_session_update_v1();
CREATE TRIGGER tr_fix_sessions_v1_no_delete BEFORE DELETE ON oms_fix_sessions_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_fix_sessions_v1_no_truncate BEFORE TRUNCATE ON oms_fix_sessions_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TABLE oms_fix_outbound_messages_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 sender_comp_id varchar(64) NOT NULL,target_comp_id varchar(64) NOT NULL,sequence bigint NOT NULL CHECK(sequence BETWEEN 1 AND 999999999),
 schema_version integer NOT NULL CHECK(schema_version=1),message_type varchar(2) NOT NULL CHECK(message_type IN('A','0','1','2','3','4','5','D','F')),
 application_message text,application_sha256 bytea,recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK((message_type IN('D','F') AND application_message IS NOT NULL AND octet_length(application_message)<=32768 AND octet_length(application_sha256)=32)
 OR (message_type NOT IN('D','F') AND application_message IS NULL AND application_sha256 IS NULL)),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,sequence),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
 REFERENCES oms_fix_sessions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
);
CREATE TRIGGER tr_fix_outbound_v1_immutable BEFORE UPDATE OR DELETE ON oms_fix_outbound_messages_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_fix_outbound_v1_no_truncate BEFORE TRUNCATE ON oms_fix_outbound_messages_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
-- Protocol cursors are separate from economic evidence. Native reports require their own versioned journal.
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_fix_sessions_v1) OR EXISTS(SELECT 1 FROM oms_fix_outbound_messages_v1) THEN RAISE EXCEPTION 'Preserve FIX session audit'; END IF; END $$;
DROP TABLE oms_fix_outbound_messages_v1; DROP TABLE oms_fix_sessions_v1; DROP FUNCTION public.oms_guard_fix_session_update_v1();
""");
}
