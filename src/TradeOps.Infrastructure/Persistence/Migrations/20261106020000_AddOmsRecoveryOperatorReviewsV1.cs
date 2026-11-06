using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106020000_AddOmsRecoveryOperatorReviewsV1")]
public sealed class AddOmsRecoveryOperatorReviewsV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Provisioned by the database owner only. No application/operator can grant itself membership here.
CREATE TABLE oms_recovery_review_permissions_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', database_role name NOT NULL,
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,database_role)
);
CREATE TABLE oms_recovery_external_reviews_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL DEFAULT '', decision_id uuid NOT NULL,
 source_run_id uuid NOT NULL, currency varchar(32) NOT NULL, native_order_id varchar(160) NOT NULL,
 evidence_sha256 bytea NOT NULL CHECK (octet_length(evidence_sha256)=32),
 decision smallint NOT NULL CHECK (decision IN (1,2)), actor_role name NOT NULL,
 reason varchar(1024) NOT NULL CHECK (length(trim(reason)) BETWEEN 1 AND 1024),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(), evidence_version bigint NOT NULL CHECK (evidence_version>0),
 PRIMARY KEY (tenant_id,venue_id,environment,account_id,subaccount_id,decision_id),
 UNIQUE (tenant_id,venue_id,environment,account_id,subaccount_id,evidence_version),
 FOREIGN KEY (tenant_id,venue_id,environment,account_id,subaccount_id,source_run_id)
 REFERENCES oms_deribit_recovery_results_v1(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
);
CREATE TRIGGER tr_recovery_external_reviews_v1_immutable BEFORE UPDATE OR DELETE ON oms_recovery_external_reviews_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_recovery_external_reviews_v1_no_truncate BEFORE TRUNCATE ON oms_recovery_external_reviews_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_evidence_version_v1 AFTER INSERT ON oms_recovery_external_reviews_v1
 FOR EACH ROW EXECUTE FUNCTION public.oms_advance_evidence_version_v1();

CREATE FUNCTION public.oms_append_recovery_external_review_v1(
 t text,v text,e smallint,a text,s text,d uuid,r uuid,c text,o text,h bytea,k smallint,n text,x bigint)
RETURNS SETOF oms_recovery_external_reviews_v1 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor name; actual bigint; prior public.oms_recovery_external_reviews_v1;
BEGIN
 actor := CASE WHEN current_setting('role')='none' THEN session_user ELSE current_setting('role') END;
 IF NOT EXISTS (SELECT 1 FROM public.oms_recovery_review_permissions_v1 p
  JOIN pg_roles roles ON roles.rolname=p.database_role
  WHERE p.tenant_id=t AND p.venue_id=v AND p.environment=e AND p.account_id=a AND p.subaccount_id=s
  AND pg_has_role(actor,roles.oid,'MEMBER')) THEN
  RAISE EXCEPTION 'Scoped recovery operator permission required' USING ERRCODE='42501';
 END IF;
 -- Same account head -> evidence lock order as publication. Only a current blocked result is reviewable.
 PERFORM 1 FROM public.oms_recovery_heads_v1 heads WHERE heads.tenant_id=t AND heads.venue_id=v AND heads.environment=e
  AND heads.account_id=a AND heads.subaccount_id=s AND heads.run_id=r
  AND (heads.state='Blocked' OR k=2 AND heads.state='Ready') FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Current blocked recovery required'; END IF;
 INSERT INTO public.oms_evidence_versions_v1 (tenant_id,venue_id,environment,account_id,subaccount_id)
 VALUES(t,v,e,a,s) ON CONFLICT DO NOTHING;
 SELECT version INTO actual FROM public.oms_evidence_versions_v1 versions WHERE versions.tenant_id=t AND versions.venue_id=v
  AND versions.environment=e AND versions.account_id=a AND versions.subaccount_id=s FOR UPDATE;
 SELECT * INTO prior FROM public.oms_recovery_external_reviews_v1 reviews WHERE reviews.tenant_id=t AND reviews.venue_id=v
  AND reviews.environment=e AND reviews.account_id=a AND reviews.subaccount_id=s AND reviews.decision_id=d;
 IF FOUND THEN
  IF prior.source_run_id<>r OR prior.currency<>c OR prior.native_order_id<>o OR prior.evidence_sha256<>h OR
   prior.decision<>k OR prior.actor_role<>actor OR prior.reason<>n THEN RAISE EXCEPTION 'Operator decision identity collision'; END IF;
  RETURN NEXT prior; RETURN;
 END IF;
 IF actual<>x THEN RAISE EXCEPTION 'Evidence changed before operator decision'; END IF;
 INSERT INTO public.oms_recovery_external_reviews_v1
 (tenant_id,venue_id,environment,account_id,subaccount_id,decision_id,source_run_id,currency,native_order_id,
  evidence_sha256,decision,actor_role,reason,evidence_version)
 VALUES(t,v,e,a,s,d,r,c,o,h,k,actor,n,actual+1) RETURNING * INTO prior;
 RETURN NEXT prior;
END; $$;
REVOKE ALL ON FUNCTION public.oms_append_recovery_external_review_v1(text,text,smallint,text,text,uuid,uuid,text,text,bytea,smallint,text,bigint) FROM PUBLIC;
-- The function authenticates the actual database role, never a caller-supplied actor string.
-- Table writes and permission configuration remain owner-only; EXECUTE alone grants no review authority.
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM oms_recovery_external_reviews_v1) OR EXISTS (SELECT 1 FROM oms_recovery_review_permissions_v1)
 THEN RAISE EXCEPTION 'Refusing destructive rollback of operator audit/permissions'; END IF;
END; $$;
DROP FUNCTION public.oms_append_recovery_external_review_v1(text,text,smallint,text,text,uuid,uuid,text,text,bytea,smallint,text,bigint);
DROP TABLE oms_recovery_external_reviews_v1;
DROP TABLE oms_recovery_review_permissions_v1;
""");
}
