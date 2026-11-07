using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261107230000_AddCrossPortfolioRiskPolicyV2")]
public sealed class AddCrossPortfolioRiskPolicyV2:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
ALTER TABLE oms_account_risk_policies_v1 DROP CONSTRAINT oms_account_risk_policies_v1_schema_version_check;
ALTER TABLE oms_account_risk_policies_v1 ADD CONSTRAINT oms_account_risk_policies_v1_schema_version_check CHECK(schema_version IN(1,2));
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_account_risk_policies_v1 WHERE schema_version=2) THEN RAISE EXCEPTION 'Preserve cross portfolio policy audit'; END IF; END $$;
ALTER TABLE oms_account_risk_policies_v1 DROP CONSTRAINT oms_account_risk_policies_v1_schema_version_check;
ALTER TABLE oms_account_risk_policies_v1 ADD CONSTRAINT oms_account_risk_policies_v1_schema_version_check CHECK(schema_version=1);
""");
}
