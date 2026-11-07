using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class OmsFixConfigurationInspectionPostgresTests
{
    [Theory]
    [InlineData("valid")][InlineData("missing-session")][InlineData("missing-policy")][InlineData("protocol-write")][InlineData("missing-node")]
    public async Task ActualFourRolesAreInspectedWithoutOwnershipOrAnyDatabaseMutation(string mode)
    {
        await using var recovery=await DeribitRecoveryHostPostgresTests.Roles.Open();var db=recovery.Db;
        await using var execution=await DeribitFixGatewayPostgresTests.Roles.Open(db);using var root=new OmsExecutionGatewayPostgresTests.Root();
        var scope=new AccountKey(new("tenant"),new("DERIBIT"),OmsEnvironment.Mainnet,new("account"));
        var key=OmsFixSessionStorePostgresTests.Key() with{Scope=scope};
        var password=Guid.NewGuid().ToString("N");
        await using(var q=db.Source.CreateCommand($"""
ALTER ROLE "{execution.execution}" LOGIN PASSWORD '{password}';
ALTER ROLE "{execution.protocol}" LOGIN PASSWORD '{password}';
GRANT CONNECT ON DATABASE "{db.Name}" TO "{execution.execution}","{execution.protocol}";
INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',4,'account','',2,'{execution.execution}',true,false);
"""))await q.ExecuteNonQueryAsync();
        Assert.Equal(4,(int)OmsEnvironment.Mainnet);
        if(mode!="missing-session")await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        if(mode!="missing-policy")
        {
            var p=DeribitRiskInputsTests.Policy();p=p with{Limits=p.Limits with{Scope=scope},ValidFromUtc=DateTimeOffset.UtcNow.AddMinutes(-1),ValidThroughUtc=DateTimeOffset.UtcNow.AddHours(1)};
            await OmsRiskPolicyAndRolePostgresTests.InsertPolicy(db,p,1);
        }
        if(mode!="missing-node")
        {
            var node=OmsExecutionNodeProvisioning.Prepare(root.PathName,scope);
            await using var q=db.Source.CreateCommand("INSERT INTO oms_execution_bindings_v2(venue_id,environment,schema_version,node_sha256,lock_root,root_sha256,lock_sha256) VALUES('DERIBIT',4,2,@node,@root,@rootHash,@fileHash)");
            q.Parameters.AddWithValue("node",node.NodeSha256);q.Parameters.AddWithValue("root",node.LockRoot);q.Parameters.AddWithValue("rootHash",node.RootSha256);q.Parameters.AddWithValue("fileHash",node.LockSha256);await q.ExecuteNonQueryAsync();
        }
        if(mode=="protocol-write"){await using var q=db.Source.CreateCommand($"GRANT INSERT ON oms_commands TO \"{execution.protocol}\"");await q.ExecuteNonQueryAsync();}
        var data=DeribitFixConfigurationCheckTests.Settings();data["DeribitOms:LockRoot"]=root.PathName;
        data["DeribitFix:SenderCompId"]=key.SenderCompId;
        data["ConnectionStrings:TradeOpsCaptureDb"]=recovery.CaptureConnection;data["ConnectionStrings:TradeOpsRecoveryDb"]=recovery.RecoveryConnection;
        data["ConnectionStrings:TradeOpsExecutionDb"]=new NpgsqlConnectionStringBuilder(db.ConnectionString){Username=execution.execution,Password=password,Options="",Pooling=false}.ConnectionString;
        data["ConnectionStrings:TradeOpsFixDb"]=new NpgsqlConnectionStringBuilder(db.ConnectionString){Username=execution.protocol,Password=password,Options="",Pooling=false}.ConnectionString;
        async Task<string> State()
        {
            await using var q=db.Source.CreateCommand("""
SELECT jsonb_build_array((SELECT count(*) FROM oms_commands),(SELECT count(*) FROM oms_outbox),
 (SELECT count(*) FROM oms_evidence_versions_v1),(SELECT count(*) FROM oms_deribit_fix_reports_v1),
 (SELECT count(*) FROM oms_private_capture_observations),(SELECT count(*) FROM oms_fix_outbound_messages_v1),
 (SELECT count(*) FROM oms_fix_amend_messages_v2),(SELECT jsonb_agg(jsonb_build_array(next_sender,next_target,last_logon_ms)) FROM oms_fix_sessions_v1))::text
""");return (string)(await q.ExecuteScalarAsync())!;
        }
        var before=await State();var files=Directory.GetFiles(root.PathName).ToDictionary(x=>x,File.GetLastWriteTimeUtc);
        var result=await DeribitFixConfigurationCheck.RunAsync(DeribitFixConfigurationCheckTests.Config(data));
        Assert.True(result.Status==(mode=="valid"?"FIX_CONFIGURATION_CHECK_PASS":"FIX_CONFIGURATION_CHECK_BLOCKED"),System.Text.Json.JsonSerializer.Serialize(result));
        var blocked=result.Checks.Where(x=>!x.Passed).Select(x=>x.Code).ToArray();
        var expected=mode switch{"missing-session"=>new[]{"ProvisionedFixSession"},"missing-policy"=>["CurrentEthAmendmentRiskPolicy"],"protocol-write"=>["ProtocolRole"],"missing-node"=>["ExistingNodeObjectBinding"],_=>[]};
        Assert.True(expected.SequenceEqual(blocked),System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(before,await State());Assert.Equal(files.Count,Directory.GetFiles(root.PathName).Length);
        foreach(var file in files)Assert.Equal(file.Value,File.GetLastWriteTimeUtc(file.Key));
        await using var locks=db.Source.CreateCommand("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND database=(SELECT oid FROM pg_database WHERE datname=current_database())");
        Assert.Equal(0L,await locks.ExecuteScalarAsync());
        Assert.False(result.VaultUsed||result.VenueUsed||result.TradingStarted||result.LiveAuthorityEstablished||result.NativeAccountVerified);
    }
}
