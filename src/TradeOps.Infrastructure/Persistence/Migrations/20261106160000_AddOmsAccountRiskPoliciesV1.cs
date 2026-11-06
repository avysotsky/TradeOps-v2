using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106160000_AddOmsAccountRiskPoliciesV1")]
public sealed class AddOmsAccountRiskPoliciesV1 : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
CREATE TABLE oms_account_risk_policies_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL, revision bigint NOT NULL CHECK(revision>0),
 schema_version integer NOT NULL CHECK(schema_version=1), policy_json jsonb NOT NULL,
 policy_sha256 bytea NOT NULL CHECK(octet_length(policy_sha256)=32), actor_role name NOT NULL DEFAULT current_user,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,revision)
);
CREATE TRIGGER tr_account_risk_policies_v1_immutable BEFORE UPDATE OR DELETE ON oms_account_risk_policies_v1 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_account_risk_policies_v1_no_truncate BEFORE TRUNCATE ON oms_account_risk_policies_v1 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE FUNCTION public.oms_lock_risk_policy_change_v1() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
 PERFORM pg_advisory_xact_lock(public.oms_risk_control_lock_key_v1(3::smallint,NEW.tenant_id::text,NEW.venue_id::text,NEW.environment,NEW.account_id::text,NEW.subaccount_id::text));
 RETURN NEW;
END $$;
CREATE TRIGGER tr_account_risk_policy_lock_v1 BEFORE INSERT ON oms_account_risk_policies_v1 FOR EACH ROW EXECUTE FUNCTION public.oms_lock_risk_policy_change_v1();
""");
    protected override void Down(MigrationBuilder b) => b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_account_risk_policies_v1) THEN RAISE EXCEPTION 'Preserve account risk policies'; END IF; END $$;
DROP TABLE oms_account_risk_policies_v1; DROP FUNCTION public.oms_lock_risk_policy_change_v1();
""");
}
