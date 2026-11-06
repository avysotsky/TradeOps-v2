using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106210000_AddDeribitFixNativeReportsV1")]
public sealed class AddDeribitFixNativeReportsV1:Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
CREATE TABLE oms_deribit_fix_reports_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 sender_comp_id varchar(64) NOT NULL,target_comp_id varchar(64) NOT NULL,connection_epoch uuid NOT NULL,sequence bigint NOT NULL CHECK(sequence BETWEEN 1 AND 1000000000),
 report_id uuid NOT NULL,schema_version integer NOT NULL CHECK(schema_version=1),message_type varchar(1) NOT NULL CHECK(message_type IN('8','9')),
 report_json jsonb NOT NULL CHECK(octet_length(report_json::text)<=65536),report_sha256 bytea NOT NULL CHECK(octet_length(report_sha256)=32),
 semantic_sha256 bytea NOT NULL CHECK(octet_length(semantic_sha256)=32),event_key bytea NOT NULL CHECK(octet_length(event_key)=32),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),actor_role name NOT NULL DEFAULT current_user,
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,report_id),
 UNIQUE(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id,connection_epoch,sequence),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
 REFERENCES oms_fix_sessions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,sender_comp_id,target_comp_id)
);
CREATE TABLE oms_deribit_fix_native_events_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 event_key bytea NOT NULL CHECK(octet_length(event_key)=32),semantic_sha256 bytea NOT NULL CHECK(octet_length(semantic_sha256)=32),first_report_id uuid NOT NULL,
 schema_version integer NOT NULL CHECK(schema_version=1),recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,event_key),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,first_report_id)
 REFERENCES oms_deribit_fix_reports_v1(tenant_id,venue_id,environment,account_id,subaccount_id,report_id)
);
CREATE TABLE oms_deribit_fix_report_incidents_v1 (
 tenant_id varchar(128) NOT NULL,venue_id varchar(64) NOT NULL,environment smallint NOT NULL,account_id varchar(128) NOT NULL,subaccount_id varchar(128) NOT NULL,
 incident_id uuid NOT NULL,schema_version integer NOT NULL CHECK(schema_version=1),reason varchar(64) NOT NULL,
 candidate_json jsonb NOT NULL CHECK(octet_length(candidate_json::text)<=65536),candidate_sha256 bytea NOT NULL CHECK(octet_length(candidate_sha256)=32),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),actor_role name NOT NULL DEFAULT current_user,
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,incident_id)
);
""");
        foreach(var table in new[]{"oms_deribit_fix_reports_v1","oms_deribit_fix_native_events_v1","oms_deribit_fix_report_incidents_v1"})
        {
            b.Sql($"CREATE TRIGGER tr_fix_native_immutable BEFORE UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();");
            b.Sql($"CREATE TRIGGER tr_fix_native_no_truncate BEFORE TRUNCATE ON {table} FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();");
        }
        foreach(var table in new[]{"oms_deribit_fix_reports_v1","oms_deribit_fix_report_incidents_v1"})
            b.Sql($"CREATE TRIGGER tr_evidence_version_v1 AFTER INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.oms_advance_evidence_version_v1();");
    }
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_deribit_fix_reports_v1) OR EXISTS(SELECT 1 FROM oms_deribit_fix_native_events_v1) OR EXISTS(SELECT 1 FROM oms_deribit_fix_report_incidents_v1) THEN RAISE EXCEPTION 'Preserve native FIX evidence'; END IF; END $$;
DROP TABLE oms_deribit_fix_report_incidents_v1; DROP TABLE oms_deribit_fix_native_events_v1; DROP TABLE oms_deribit_fix_reports_v1;
""");
}
