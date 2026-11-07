using System.Diagnostics;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using FrozenClock=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;
public sealed class OmsExecutionGatewayPostgresTests
{
    internal sealed class Recovery:IOmsExecutionRecoverySource
    { public OmsLiveExecutionBaseline? Current {get;set;} internal int Refreshes;public void RequestRecovery(){Current=null;Refreshes++;} }
    internal sealed class Wire:IOmsAccountOrderTransport
    {
        internal int Writes;internal bool LoseAck;internal Func<Task>? BeforeWrite;internal Func<Task>? AfterWrite;internal bool Aborted;
        public async Task<DeribitJsonRpcCommandReceipt> SendAsync(OmsJournalCommandRequest c,OmsPhysicalWritePermit permit,CancellationToken ct)
        {
            if(BeforeWrite is not null) await BeforeWrite();
            await permit.InitiateAsync(c,()=> { if(Aborted) throw new IOException("Closed wire.");Writes++;return Task.CompletedTask; });
            if(AfterWrite is not null)await AfterWrite();
            if(LoseAck)throw new IOException("Synthetic lost ACK.");
            return DeribitCommandReceiptFixtures.Receipt(c,sent:DateTimeOffset.UtcNow);
        }
        public void Abort()=>Aborted=true;
        public ValueTask DisposeAsync(){Aborted=true;return ValueTask.CompletedTask;}
    }
    internal sealed class Root:IDisposable
    {
        internal readonly string PathName=Path.Combine(Path.GetTempPath(),"tradeops-b5-"+Guid.NewGuid().ToString("N"));
        internal Root()=>Directory.CreateDirectory(PathName);
        internal async Task Bind(NpgsqlDataSource source,TradeOps.Domain.Oms.AccountKey? scope=null)
        {
            scope??=Scope();var binding=OmsExecutionNodeProvisioning.Prepare(PathName,scope);
            await using var q=source.CreateCommand("INSERT INTO oms_execution_bindings_v2(venue_id,environment,schema_version,node_sha256,lock_root,root_sha256,lock_sha256) VALUES('DERIBIT',@env,2,@node,@root,@rootHash,@fileHash)");
            q.Parameters.AddWithValue("env",(short)scope.Environment);q.Parameters.AddWithValue("node",binding.NodeSha256);
            q.Parameters.AddWithValue("root",binding.LockRoot);q.Parameters.AddWithValue("rootHash",binding.RootSha256);q.Parameters.AddWithValue("fileHash",binding.LockSha256);await q.ExecuteNonQueryAsync();
        }
        public void Dispose()
        {
            var full=Path.GetFullPath(PathName);
            if(Path.GetDirectoryName(full)!=Path.TrimEndingDirectorySeparator(Path.GetTempPath())||!Path.GetFileName(full).StartsWith("tradeops-b5-",StringComparison.Ordinal))throw new InvalidOperationException("Foreign cleanup root.");
            foreach(var file in Directory.GetFiles(full))
            { if(Path.GetDirectoryName(Path.GetFullPath(file))!=full||Path.GetExtension(file)!=".lock")throw new InvalidOperationException("Foreign lock file.");File.Delete(file); }
            Directory.Delete(full);
        }
    }
    internal static OmsJournalCommandRequest Command()
    { var c=DeribitCommandReceiptFixtures.Command() with{Scope=Scope()};return c with{Payload=c.Payload with{TargetClientOrderId="oms-"+c.LocalOrderId.Value.ToString("N")}}; }
    internal static async Task<long> Count(OmsPostgresTestDatabase db,string table)
    {await using var q=db.Source.CreateCommand("SELECT count(*) FROM "+table);return(long)(await q.ExecuteScalarAsync())!;}
    internal static async Task<Recovery> Prepare(Live live)
    {var(t,d,q)=await live.Prepare();var p=await live.Store.PublishAsync(t,d,q);return new(){Current=new(p,q)};}

    [Fact]
    public async Task NativeEvidenceCanCommitAfterPhysicalInitiationBeforeResponseWithoutCounterDeadlock()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var command=Command();var wire=new Wire();
        wire.AfterWrite=async()=>
        {
            await using var foreign=await db.Source.OpenConnectionAsync();
            await using(var timeout=new NpgsqlCommand("SET lock_timeout='2s'",foreign))await timeout.ExecuteNonQueryAsync();
            await using var update=new NpgsqlCommand("UPDATE oms_commands SET updated_at=clock_timestamp() WHERE command_id=@command",foreign);
            update.Parameters.AddWithValue("command",command.CommandId.Value);Assert.Equal(1,await update.ExecuteNonQueryAsync());
        };
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
        Assert.Equal(OmsExecutionResultKind.Acknowledged,(await gateway.SubmitAsync(command)).Kind);Assert.Equal(1,wire.Writes);
    }
    [Fact]
    public async Task OldTextOnlyBindingCannotAuthorizePhysicalOwnership()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();
        await using var q=db.Source.CreateCommand("INSERT INTO oms_execution_bindings_v1 VALUES('DERIBIT',@env,1,@node,@root)");
        q.Parameters.AddWithValue("env",(short)Scope().Environment);q.Parameters.AddWithValue("node",OmsSingleHostPhysicalFence.NodeSha256());
        q.Parameters.AddWithValue("root",OmsSingleHostPhysicalFence.CanonicalRoot(root.PathName));await q.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));
        Assert.Empty(Directory.GetFiles(root.PathName));
    }
    [Fact]
    public async Task ReplacedDirectoryAtSameTextPathCannotAuthorizeAnotherFilesystemObject()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        using var moved=new Root();Directory.Delete(moved.PathName);Directory.Move(root.PathName,moved.PathName);Directory.CreateDirectory(root.PathName);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));
        Assert.Empty(Directory.GetFiles(root.PathName));
    }
    [Fact]
    public async Task UnlinkedAndRecreatedFileCannotStealOwnerFromPinnedPhysicalObject()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        await using var first=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);var file=Assert.Single(Directory.GetFiles(root.PathName));
        if(OperatingSystem.IsWindows())Assert.Throws<IOException>(()=>File.Delete(file));
        else
        {
            File.Delete(file);using(var replacement=new FileStream(file,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)){}
            await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));
        }
        Assert.True(first.IsCurrent);
    }
    [Fact]
    public async Task DurableCommandAttemptReservationPrecedeWriteAndConcurrentRetriesNeverResend()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var wire=new Wire();
        wire.BeforeWrite=async()=>{Assert.Equal(1,await Count(db,"oms_commands"));Assert.Equal(1,await Count(db,"oms_attempts"));Assert.Equal(1,await Count(db,"oms_execution_dispatches_v2"));Assert.Equal(1,await Count(db,"oms_risk_reservations_v1"));};
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
        var command=Command();var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>gateway.SubmitAsync(command)));
        Assert.Single(results,x=>x.Kind==OmsExecutionResultKind.Acknowledged);Assert.Equal(1,wire.Writes);
        Assert.Equal(1,await Count(db,"oms_deribit_command_receipts_v1"));Assert.True(recovery.Refreshes>=1);
    }
    [Fact]
    public async Task UnknownWriteRetainsPendingAndRestartCannotBlindlyResend()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var original=recovery.Current;var wire=new Wire{LoseAck=true};var command=Command();
        await using(var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock))
        {Assert.Equal(OmsExecutionResultKind.Unknown,(await gateway.SubmitAsync(command)).Kind);Assert.Equal(1,wire.Writes);Assert.True(wire.Aborted);}
        Assert.Equal(1,await Count(db,"oms_risk_reservations_v1"));Assert.Equal(0,await Count(db,"oms_deribit_command_receipts_v1"));
        recovery.Current=original;var next=new Wire();
        await using var restarted=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(next),clock);
        Assert.Equal(OmsExecutionResultKind.Denied,(await restarted.SubmitAsync(command)).Kind);Assert.Equal(0,next.Writes);
    }
    [Fact]
    public async Task ForeignJournalMutationCannotCommitBetweenFinalEvidenceCheckAndPhysicalWrite()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var command=Command();var wire=new Wire();
        bool foreignCommitted=false;string? foreignSqlState=null;
        wire.BeforeWrite=async()=>
        {
            await using var foreign=await db.Source.OpenConnectionAsync();
            await using(var timeout=new NpgsqlCommand("SET lock_timeout='250ms'",foreign))await timeout.ExecuteNonQueryAsync();
            await using var update=new NpgsqlCommand("UPDATE oms_commands SET updated_at=clock_timestamp() WHERE command_id=@command",foreign);
            update.Parameters.AddWithValue("command",command.CommandId.Value);
            try{foreignCommitted=await update.ExecuteNonQueryAsync()==1;}
            catch(PostgresException failure){foreignSqlState=failure.SqlState;}
        };
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
        var result=await gateway.SubmitAsync(command);
        Assert.False(foreignCommitted);Assert.Equal("55P03",foreignSqlState);
        Assert.Equal(OmsExecutionResultKind.Acknowledged,result.Kind);Assert.Equal(1,wire.Writes);
    }
    [Fact]
    public async Task NewIngressImmediatelyBeforePhysicalWriteRejectsPermitAndRetainsUnknownAttempt()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var wire=new Wire();
        wire.BeforeWrite=async()=>{var epoch=recovery.Current!.Qualified.Start.Epoch;await live.Coordinator.RegisterReceiptAsync(live.Session,epoch,OmsPrivateCaptureReplayTests.Event(epoch));};
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
        Assert.Equal(OmsExecutionResultKind.Unknown,(await gateway.SubmitAsync(Command())).Kind);Assert.Equal(0,wire.Writes);
        Assert.Equal(1,await Count(db,"oms_attempts"));Assert.Equal(1,await Count(db,"oms_risk_reservations_v1"));
    }
    [Fact]
    public async Task MissingNodePinNeverOpensTradingTransportAndOtherRootCannotStealOwner()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();var opened=0;
        Task<IOmsAccountOrderTransport> Open(CancellationToken ct){opened++;return Task.FromResult<IOmsAccountOrderTransport>(new Wire());}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,new Recovery(),new Inputs(),root.PathName,Open));Assert.Equal(0,opened);
        await root.Bind(db.Source);using var other=new Root();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,new Recovery(),new Inputs(),other.PathName,Open));Assert.Equal(0,opened);
    }
    [Fact]
    public async Task RevokedOwnerAndConsumedPermitCannotWriteAfterSuccessfulHandover()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);
        var first=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);var c=Command();
        var permit=recovery.Current!.Qualified.CreatePhysicalWritePermit(c,()=>first.IsCurrent);await first.DisposeAsync();
        await using var second=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope("another-logical-account"),root.PathName);
        var writes=0;await Assert.ThrowsAsync<InvalidOperationException>(()=>permit.InitiateAsync(c,()=>{writes++;return Task.CompletedTask;}));Assert.Equal(0,writes);
        var valid=recovery.Current.Qualified.CreatePhysicalWritePermit(c,()=>second.IsCurrent);
        await valid.InitiateAsync(c,()=>{writes++;return Task.CompletedTask;});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>valid.InitiateAsync(c,()=>{writes++;return Task.CompletedTask;}));Assert.Equal(1,writes);
    }
    [Fact]
    public async Task ActualChildProcessCannotTakeOwnerAndProcessDeathReleasesItsOsHandle()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var first=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);var file=Assert.Single(Directory.GetFiles(root.PathName));
        using(var denied=StartProbe(file)){Assert.Equal("BUSY",await denied.StandardOutput.ReadLineAsync());await denied.WaitForExitAsync();Assert.Equal(2,denied.ExitCode);}
        await first.DisposeAsync();
        using var child=StartProbe(file);
        try
        {
            Assert.Equal("ACQUIRED",await child.StandardOutput.ReadLineAsync());
            await Assert.ThrowsAsync<IOException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));
        }
        finally {if(!child.HasExited)child.Kill();await child.WaitForExitAsync();}
        await using var next=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);Assert.True(next.IsCurrent);
    }
    private static Process StartProbe(string file)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath?.EndsWith("dotnet",StringComparison.OrdinalIgnoreCase)==true?Environment.ProcessPath:"dotnet")
        {RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("exec");start.ArgumentList.Add("--runtimeconfig");start.ArgumentList.Add(Path.ChangeExtension(typeof(OmsExecutionGatewayPostgresTests).Assembly.Location,"runtimeconfig.json"));
        start.ArgumentList.Add(typeof(TradeOps.OmsPhysicalOwnerProbe.Probe).Assembly.Location);start.ArgumentList.Add(file);
        return Process.Start(start)??throw new InvalidOperationException("Owned child process did not start.");
    }
    [Fact]
    public async Task DisposalCannotReleasePhysicalFenceWhileAnOldOperationIsPausedBeforeWrite()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await Prepare(live);var wire=new Wire();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.BeforeWrite=async()=>{entered.SetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(10));};
        var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),clock);
        var send=gateway.SubmitAsync(Command());await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing=gateway.DisposeAsync().AsTask();
        try {await Assert.ThrowsAsync<IOException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));Assert.False(closing.IsCompleted);}
        finally {release.TrySetResult();}
        Assert.Equal(OmsExecutionResultKind.Unknown,(await send).Kind);await closing;Assert.Equal(0,wire.Writes);
        await using var next=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);Assert.True(next.IsCurrent);
    }
    [Fact]
    public async Task NoLiveBaselineAndWrongScopeAreAuditedWithoutAnyWriteOrSyntheticApproval()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new Root();await root.Bind(db.Source);var wire=new Wire();
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,new Recovery(),new Inputs(),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire));
        Assert.Equal(OmsExecutionResultKind.Denied,(await gateway.SubmitAsync(Command())).Kind);
        Assert.Equal(OmsExecutionResultKind.Denied,(await gateway.SubmitAsync(Command() with{Scope=Scope("other-account")})).Kind);
        Assert.Equal(0,wire.Writes);Assert.Equal(0,await Count(db,"oms_commands"));Assert.Equal(2,await Count(db,"oms_risk_admission_audit_v1"));
        await using var q=db.Source.CreateCommand("SELECT bool_and(result=5 AND baseline_run_id='00000000-0000-0000-0000-000000000000') FROM oms_risk_admission_audit_v1");
        Assert.Equal(true,await q.ExecuteScalarAsync());
    }
}
