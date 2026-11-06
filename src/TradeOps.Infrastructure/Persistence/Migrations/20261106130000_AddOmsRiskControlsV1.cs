using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261106130000_AddOmsRiskControlsV1")]
public sealed class AddOmsRiskControlsV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Permission provisioning is database-owner work, never a host startup operation.
CREATE TABLE oms_risk_control_permissions_v1 (
 level smallint NOT NULL CHECK(level BETWEEN 1 AND 3), tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL, account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL,
 kind smallint NOT NULL CHECK(kind IN(1,2)), database_role name NOT NULL, can_activate boolean NOT NULL, can_clear boolean NOT NULL,
 PRIMARY KEY(level,tenant_id,venue_id,environment,account_id,subaccount_id,kind,database_role)
);
CREATE TABLE oms_risk_control_events_v1 (
 level smallint NOT NULL CHECK(level BETWEEN 1 AND 3), tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL,
 environment smallint NOT NULL, account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL,
 kind smallint NOT NULL CHECK(kind IN(1,2)), decision_id uuid NOT NULL CHECK(decision_id<>'00000000-0000-0000-0000-000000000000'),
 expected_revision bigint NOT NULL CHECK(expected_revision>=0), revision bigint NOT NULL CHECK(revision=expected_revision+1),
 blocked boolean NOT NULL, reason varchar(1024) NOT NULL CHECK(length(trim(reason)) BETWEEN 1 AND 1024),
 schema_version integer NOT NULL CHECK(schema_version=1), actor_role name NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(level,tenant_id,venue_id,environment,account_id,subaccount_id,kind,revision),
 UNIQUE(level,tenant_id,venue_id,environment,account_id,subaccount_id,kind,decision_id),
 CHECK((level=1 AND tenant_id='' AND venue_id='' AND environment=0 AND account_id='' AND subaccount_id='') OR
       (level=2 AND tenant_id<>'' AND venue_id<>'' AND environment BETWEEN 1 AND 4 AND account_id='' AND subaccount_id='') OR
       (level=3 AND tenant_id<>'' AND venue_id<>'' AND environment BETWEEN 1 AND 4 AND account_id<>''))
);
CREATE TRIGGER tr_risk_control_events_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_control_events_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_control_events_v1_no_truncate BEFORE TRUNCATE ON oms_risk_control_events_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();

CREATE TABLE oms_risk_admission_controls_v1 (
 tenant_id varchar(128) NOT NULL, venue_id varchar(64) NOT NULL, environment smallint NOT NULL,
 account_id varchar(128) NOT NULL, subaccount_id varchar(128) NOT NULL, command_id uuid NOT NULL,
 schema_version integer NOT NULL CHECK(schema_version=1), stamp_json jsonb NOT NULL,
 stamp_sha256 bytea NOT NULL CHECK(octet_length(stamp_sha256)=32),
 PRIMARY KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id),
 FOREIGN KEY(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
 REFERENCES oms_risk_reservations_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
);
CREATE TRIGGER tr_risk_admission_controls_v1_immutable BEFORE UPDATE OR DELETE ON oms_risk_admission_controls_v1
 FOR EACH ROW EXECUTE FUNCTION oms_deny_row_mutation();
CREATE TRIGGER tr_risk_admission_controls_v1_no_truncate BEFORE TRUNCATE ON oms_risk_admission_controls_v1
 FOR EACH STATEMENT EXECUTE FUNCTION oms_deny_row_mutation();

CREATE FUNCTION public.oms_risk_control_lock_key_v1(l smallint,t text,v text,e smallint,a text,s text)
RETURNS bigint LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT hashtextextended(jsonb_build_array('oms-risk-control-v1',l,t,v,e,a,s)::text,61734591);
$$;
REVOKE ALL ON FUNCTION public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text) FROM PUBLIC;

CREATE FUNCTION public.oms_set_risk_control_v1(l smallint,t text,v text,e smallint,a text,s text,
 k smallint,d uuid,x bigint,b boolean,n text)
RETURNS SETOF oms_risk_control_events_v1 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor name; actual bigint; prior public.oms_risk_control_events_v1;
BEGIN
 actor := CASE WHEN current_setting('role')='none' THEN session_user ELSE current_setting('role') END;
 -- Exact role membership and scope. An activate-only responder cannot clear a latch.
 IF NOT EXISTS(SELECT 1 FROM public.oms_risk_control_permissions_v1 p JOIN pg_roles r ON r.rolname=p.database_role
  WHERE p.level=l AND p.tenant_id=t AND p.venue_id=v AND p.environment=e AND p.account_id=a AND p.subaccount_id=s
  AND p.kind=k AND pg_has_role(actor,r.oid,'MEMBER') AND (b AND p.can_activate OR NOT b AND p.can_clear)) THEN
  RAISE EXCEPTION 'Scoped risk control permission required' USING ERRCODE='42501';
 END IF;
 IF l IS NULL OR t IS NULL OR v IS NULL OR e IS NULL OR a IS NULL OR s IS NULL OR k IS NULL OR
  d IS NULL OR x IS NULL OR b IS NULL OR n IS NULL OR k NOT IN(1,2) OR x<0 OR
  n<>btrim(n) OR n ~ '[[:cntrl:]]' OR length(n) NOT BETWEEN 1 AND 1024 OR
  NOT ((l=1 AND t='' AND v='' AND e=0 AND a='' AND s='') OR
       (l=2 AND t<>'' AND v<>'' AND e BETWEEN 1 AND 4 AND a='' AND s='') OR
       (l=3 AND t<>'' AND v<>'' AND e BETWEEN 1 AND 4 AND a<>'')) THEN
  RAISE EXCEPTION 'Invalid risk control decision';
 END IF;
 PERFORM pg_advisory_xact_lock(public.oms_risk_control_lock_key_v1(l,t,v,e,a,s));
 SELECT * INTO prior FROM public.oms_risk_control_events_v1 h WHERE h.level=l AND h.tenant_id=t AND h.venue_id=v
  AND h.environment=e AND h.account_id=a AND h.subaccount_id=s AND h.kind=k AND h.decision_id=d;
 IF FOUND THEN
  IF prior.expected_revision<>x OR prior.blocked<>b OR prior.reason<>n OR prior.actor_role<>actor THEN
   RAISE EXCEPTION 'Risk control decision identity collision'; END IF;
  RETURN NEXT prior; RETURN;
 END IF;
 SELECT COALESCE(max(h.revision),0) INTO actual FROM public.oms_risk_control_events_v1 h WHERE h.level=l AND h.tenant_id=t
  AND h.venue_id=v AND h.environment=e AND h.account_id=a AND h.subaccount_id=s AND h.kind=k;
 IF actual<>x THEN RAISE EXCEPTION 'Risk control revision changed' USING ERRCODE='40001'; END IF;
 INSERT INTO public.oms_risk_control_events_v1(level,tenant_id,venue_id,environment,account_id,subaccount_id,kind,
  decision_id,expected_revision,revision,blocked,reason,schema_version,actor_role)
 VALUES(l,t,v,e,a,s,k,d,x,x+1,b,n,1,actor) RETURNING * INTO prior;
 RETURN NEXT prior;
END; $$;
REVOKE ALL ON FUNCTION public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text) FROM PUBLIC;
-- Table writes stay owner-only. Granting EXECUTE alone does not grant an operator permission.
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM oms_risk_control_events_v1) OR EXISTS(SELECT 1 FROM oms_risk_control_permissions_v1)
 OR EXISTS(SELECT 1 FROM oms_risk_admission_controls_v1) THEN
 RAISE EXCEPTION 'Refusing destructive risk controls rollback: preserve audit and stop state'; END IF;
END $$;
DROP FUNCTION public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text);
DROP FUNCTION public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text);
DROP TABLE oms_risk_admission_controls_v1;
DROP TABLE oms_risk_control_events_v1;
DROP TABLE oms_risk_control_permissions_v1;
""");
}
