using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitFixNontradingPostgresTests
{
    [Fact]
    public async Task RealLimitedLoginsPhysicalOwnerAndProtocolStoreOnlyPersistAdministrativeTraffic()
    {
        await using var recovery=await DeribitRecoveryHostPostgresTests.Roles.Open();var db=recovery.Db;
        await using var roles=await DeribitFixGatewayPostgresTests.Roles.Open(db);using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        var password=Guid.NewGuid().ToString("N");
        await using(var q=db.Source.CreateCommand($"ALTER ROLE \"{roles.execution}\" LOGIN PASSWORD '{password}'; ALTER ROLE \"{roles.protocol}\" LOGIN PASSWORD '{password}'; GRANT CONNECT ON DATABASE \"{db.Name}\" TO \"{roles.execution}\",\"{roles.protocol}\"; INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',3,'account','',2,'{roles.execution}',true,false);"))await q.ExecuteNonQueryAsync();
        await using var execution=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Username=roles.execution,Password=password,Options="",Pooling=true,MaxPoolSize=4}.ConnectionString);
        await using var protocol=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Username=roles.protocol,Password=password,Options="",Pooling=true,MaxPoolSize=4}.ConnectionString);
        await using var captureSource=NpgsqlDataSource.Create(recovery.CaptureConnection);await using var recoverySource=NpgsqlDataSource.Create(recovery.RecoveryConnection);
        await OmsPostgresPrivateCaptureOwner.ValidateRoleAsync(captureSource);await OmsPostgresRecoveryRuntimeGuard.ValidateAsync(recoverySource);
        await OmsPostgresExecutionRuntimeGuard.ValidateAsync(execution,scope:Scope());await OmsPostgresFixRuntimeGuardV1.ValidateAsync(protocol);
        foreach(var source in new[]{execution,protocol,captureSource,recoverySource})
        {await using var identity=source.CreateCommand("SELECT current_user=session_user");Assert.Equal(true,await identity.ExecuteScalarAsync());}
        var key=OmsFixSessionStorePostgresTests.Key("nontrading-"+Guid.NewGuid().ToString("N"));await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        await using var owner=await OmsSingleHostPhysicalFence.AcquireAsync(execution,Scope(),root.PathName);
        await Assert.ThrowsAsync<IOException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(execution,Scope(),root.PathName));
        await using var captureOwner=await OmsPostgresPrivateCaptureOwner.AcquireAsync(captureSource,Scope());
        await using var coordinator=new OmsPrivateCaptureCoordinator(captureOwner,new());var session=await coordinator.SwitchAsync(Scope());
        using var store=new OmsPostgresFixSessionStoreV1(protocol,key);using var handler=new DeribitFixConnectionProbeTests.PinHttp("valid");using var http=new HttpClient(handler);
        var wire=new DeribitFixConnectionProbeTests.PeerWire(key,"valid",store);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result=await DeribitFixConnectionProbeV1.RunCoreAsync(DeribitFixGatewayPostgresTests.Binding(),new(key),store,new OmsPostgresDeribitFixReportJournalV1(protocol,key),
            owner.CreateSessionAuthority(),()=>coordinator.BeginExternalIngress(session),http,()=>{},_=>Task.FromResult<IDeribitFixWireV1>(wire),deadline.Token);
        Assert.True(result.Status=="FIX_NONTRADING_CONNECTION_PASS",System.Text.Json.JsonSerializer.Serialize(result)+"; admin types="+string.Join(",",wire.Types));Assert.Equal(new[]{"A","1","5"},wire.Types);
        await using(var q=protocol.CreateCommand("SELECT next_sender,next_target FROM oms_fix_sessions_v1"))
        {await using var rows=await q.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal(4L,rows.GetInt64(0));Assert.Equal(4L,rows.GetInt64(1));}
        await using(var q=protocol.CreateCommand("SELECT count(*),bool_and(application_message IS NULL AND application_sha256 IS NULL) FROM oms_fix_outbound_messages_v1"))
        {await using var rows=await q.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal(3L,rows.GetInt64(0));Assert.True(rows.GetBoolean(1));}
        foreach(var table in new[]{"oms_commands","oms_attempts","oms_outbox","oms_risk_reservations_v1","oms_deribit_fix_command_bindings_v1","oms_deribit_fix_reports_v1"})
        {await using var q=db.Source.CreateCommand($"SELECT count(*) FROM {table}");Assert.Equal(0L,await q.ExecuteScalarAsync());}
        await using(var denied=protocol.CreateCommand("INSERT INTO oms_commands DEFAULT VALUES"))
            Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
        Assert.False(result.TradingReady||result.TradingStarted);Assert.Equal(0,result.BusinessMessagesSent);
    }
}
