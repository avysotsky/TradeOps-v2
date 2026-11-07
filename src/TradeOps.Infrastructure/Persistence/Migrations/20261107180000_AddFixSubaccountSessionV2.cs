using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace TradeOps.Infrastructure.Persistence.Migrations;
[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261107180000_AddFixSubaccountSessionV2")]
public sealed class AddFixSubaccountSessionV2:Migration
{
    protected override void Up(MigrationBuilder b)=>b.Sql("""
ALTER TABLE oms_fix_sessions_v1 DROP CONSTRAINT oms_fix_sessions_v1_schema_version_check;
ALTER TABLE oms_fix_sessions_v1 ADD CONSTRAINT oms_fix_sessions_v1_schema_version_check
 CHECK((schema_version=1 AND subaccount_id='') OR (schema_version=2 AND subaccount_id<>'' AND subaccount_id=account_id));
""");
    protected override void Down(MigrationBuilder b)=>b.Sql("""
DO $$ BEGIN IF EXISTS(SELECT 1 FROM oms_fix_sessions_v1 WHERE schema_version=2) THEN RAISE EXCEPTION 'Preserve scoped FIX session v2'; END IF; END $$;
ALTER TABLE oms_fix_sessions_v1 DROP CONSTRAINT oms_fix_sessions_v1_schema_version_check;
ALTER TABLE oms_fix_sessions_v1 ADD CONSTRAINT oms_fix_sessions_v1_schema_version_check CHECK(schema_version=1);
""");
}
