using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Exclusive single-host OMS executor. Same physical owner for every transport on a venue/environment.
/// Fail-stop, no expiry takeover, no resend. Production composition provisions node/root and restricted roles separately.</summary>
public sealed class OmsPostgresExecutionGateway : IOmsOrderIntentGateway, IAsyncDisposable
{
    private readonly AccountKey scope;
    private readonly NpgsqlDataSource source;
    private readonly IOmsExecutionRecoverySource recovery;
    private readonly IOmsRiskInputsReader inputs;
    private readonly OmsSingleHostPhysicalFence fence;
    private readonly IOmsAccountOrderTransport transport;
    private readonly TimeProvider clock;
    private readonly IOmsRiskPolicyReader? cancelPolicies;
    private OmsPostgresExecutionGateway(AccountKey scope, NpgsqlDataSource source, IOmsExecutionRecoverySource recovery,
        IOmsRiskInputsReader inputs, OmsSingleHostPhysicalFence fence, IOmsAccountOrderTransport transport, TimeProvider clock,IOmsRiskPolicyReader? cancelPolicies)
    { this.scope=scope; this.source=source; this.recovery=recovery; this.inputs=inputs; this.fence=fence; this.transport=transport; this.clock=clock;this.cancelPolicies=cancelPolicies; }

    public static async Task<OmsPostgresExecutionGateway> OpenAsync(AccountKey scope, NpgsqlDataSource source,
        IOmsExecutionRecoverySource recovery, IOmsRiskInputsReader inputs, string lockRoot,
        Func<CancellationToken,Task<IOmsAccountOrderTransport>> openTransport, TimeProvider? clock = null,IOmsRiskPolicyReader? cancelPolicies=null, CancellationToken ct = default)
    {
        var fence = await OmsSingleHostPhysicalFence.AcquireAsync(source, scope, lockRoot, ct);
        try
        {
            var transport = await fence.RunAsync(() => openTransport(ct),ct);
            return new(scope,source,recovery,inputs,fence,transport,clock ?? TimeProvider.System,cancelPolicies);
        }
        catch { await fence.DisposeAsync(); throw; }
    }

    public Task<OmsExecutionResult> SubmitAsync(OmsJournalCommandRequest command, CancellationToken ct = default)
    {
        command.Validate();
        if (command.Scope != scope) return RejectScopeAsync(command,ct);
        return fence.RunAsync<OmsExecutionResult>(async () =>
        {
            var live = recovery.Current;
            if (live is null || !live.Qualified.IsCurrent) { await OmsPostgresRiskAdmission.AuditUnavailableAsync(source,command,ct);recovery.RequestRecovery(); return new(OmsExecutionResultKind.Denied,null,"Fresh live recovery required."); }
            var isCancel=command.Payload.Kind==OmsJournalCommandKind.Cancel;
            var admission = await new OmsPostgresRiskAdmission(source,inputs,clock,cancelPolicies).AdmitAsync(command,live.Publication,live.Qualified,ct);
            if (admission != OmsRiskAdmissionResult.Reserved)
            {if(admission==OmsRiskAdmissionResult.StaleBaseline||!live.Qualified.IsCurrent)recovery.RequestRecovery();return new(OmsExecutionResultKind.Denied,admission,"Risk admission denied or command already exists; no resend.");}
            bool started = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var token = deadline.Token;
                await using var db = await source.OpenConnectionAsync(token);
                long dispatchVersion;
                await using (var tx = await db.BeginTransactionAsync(token))
                {
                    var controls = await OmsPostgresRiskControls.LockAndReadAsync(db,tx,scope,token);
                    if (!await OmsPostgresRiskControls.MatchesAdmissionAsync(db,tx,scope,command.CommandId,controls,token,isCancel) ||
                        !await LockBaseline(db,tx,live,token)) return await RejectDispatchAsync(command,live,admission,"Admission invalidated before dispatch.",token);
                    await using (var all = Query(db,tx,"SELECT command_id FROM oms_commands WHERE " + Where + " ORDER BY command_id FOR UPDATE"))
                    { await using var rows = await all.ExecuteReaderAsync(token); while(await rows.ReadAsync(token)) { } }
                    long actual;
                    await using (var q = Query(db,tx,"SELECT public.oms_lock_evidence_version_v1(@t,@v,@e,@a,@s)")) actual=(long)(await q.ExecuteScalarAsync(token))!;
                    await using (var q = Query(db,tx,"SELECT max(evidence_after) FROM (SELECT evidence_after FROM oms_risk_reservations_v1 WHERE " + Where + " AND run_id=@run AND generation=@generation AND state_sha256=@sha UNION ALL SELECT evidence_after FROM oms_risk_cancel_admissions_v1 WHERE "+Where+" AND run_id=@run AND generation=@generation AND state_sha256=@sha) chain"))
                    {
                        BindLive(q,live);
                        if (await q.ExecuteScalarAsync(token) is not long expected || actual!=expected) return await RejectDispatchAsync(command,live,admission,"Journal changed before dispatch.",token);
                    }
                    if (await OmsPostgresJournal.MarkStartedInTransactionAsync(db,tx,scope,command.CommandId,null,token)!=OmsJournalAttemptResult.Started)
                        return await RejectDispatchAsync(command,live,admission,"Command no longer dispatchable.",token);
                    await using (var q=Query(db,tx,"SELECT version FROM oms_evidence_versions_v1 WHERE " + Where)) dispatchVersion=(long)(await q.ExecuteScalarAsync(token))!;
                    await using (var q=Query(db,tx,"""
INSERT INTO oms_execution_dispatches_v2(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,schema_version,evidence_after_started,owner_id)
VALUES(@t,@v,@e,@a,@s,@command,2,@version,@owner)
"""))
                    { q.Parameters.AddWithValue("command",command.CommandId.Value); q.Parameters.AddWithValue("version",dispatchVersion); q.Parameters.AddWithValue("owner",fence.OwnerId); await q.ExecuteNonQueryAsync(token); }
                    // Unknown COMMIT ACK is conservatively treated as possibly started, never retried.
                    started=true; await tx.CommitAsync(token);
                }
                DeribitJsonRpcCommandReceipt receipt;
                Task<DeribitJsonRpcCommandReceipt> response;
                await using (var tx=await db.BeginTransactionAsync(token))
                {
                    var controls=await OmsPostgresRiskControls.LockAndReadAsync(db,tx,scope,token);
                    if (!await OmsPostgresRiskControls.MatchesAdmissionAsync(db,tx,scope,command.CommandId,controls,token,isCancel) ||
                        !await LockBaseline(db,tx,live,token)) throw new InvalidOperationException("Dispatch gates changed.");
                    // Hold the counter through the socket write, so a foreign journal transition
                    // cannot commit after this comparison and before physical initiation.
                    await using (var q=Query(db,tx,"SELECT public.oms_lock_evidence_version_v1(@t,@v,@e,@a,@s)"))
                        if (await q.ExecuteScalarAsync(token) is not long actual || actual!=dispatchVersion) throw new InvalidOperationException("Dispatch evidence changed.");
                    var state=OmsRecoveryRunCodec.Decode<OmsRecoveredAccountStateV1>(live.Qualified.StateJson,live.Qualified.StateSha256);
                    var risk=isCancel?null:await inputs.ReadAsync(command,state,token); decimal pending,margin; long count;
                    await using (var q=Query(db,tx,"""
SELECT COALESCE(sum(reserved_usd),0),COALESCE(sum(margin_usd),0),count(*) FROM oms_risk_reservations_v1
WHERE tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s AND command_id<>@command
AND command_id NOT IN(SELECT command_id FROM oms_risk_settlements_v1 WHERE tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s)
"""))
                    { q.Parameters.AddWithValue("command",command.CommandId.Value); await using var r=await q.ExecuteReaderAsync(token); await r.ReadAsync(token); pending=r.GetDecimal(0); margin=r.GetDecimal(1); count=r.GetInt64(2); }
                    bool allowed;
                    if(isCancel)
                    {
                        var parent=await OmsPostgresRiskAdmission.ReadCancelParentAsync(db,tx,command,token);
                        var policy=cancelPolicies is null?null:await cancelPolicies.ReadAsync(scope,token);
                        allowed=parent is not null&&OmsAddressedCancelPolicyV1.Allows(command,parent,state,policy,clock.GetUtcNow());
                    }
                    else allowed=risk is not null&&OmsRiskLimitsV1.Allows(command,live.Publication.StateSha256,risk,pending,margin,count,clock.GetUtcNow());
                    if(!allowed)
                        throw new InvalidOperationException("Risk changed before physical send.");
                    var permit=live.Qualified.CreatePhysicalWritePermit(command,()=>fence.IsCurrent);
                    // The engine may synchronously wait for its physical write. Keep the SQL
                    // owner free to commit as soon as the permit observes actual I/O initiation.
                    response=Task.Run(()=>transport.SendAsync(command,permit,token),CancellationToken.None);
                    _=response.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                    try
                    {
                        var first=await Task.WhenAny(permit.PhysicalIoInitiated,response).WaitAsync(token);
                        if(first==response) { await response; if(!permit.PhysicalIoInitiated.IsCompletedSuccessfully) throw new InvalidOperationException("Transport returned without physical I/O."); }
                        await permit.PhysicalIoInitiated.WaitAsync(token);
                        await tx.CommitAsync(token);
                    }
                    catch
                    {
                        // Revoke queued/paused writes before rollback releases the SQL gates.
                        fence.Revoke();transport.Abort();throw;
                    }
                }
                receipt=await response.WaitAsync(token);
                var stored=await new OmsPostgresDeribitCommandReceiptJournal(source).RecordAsync(receipt,ct);
                if (stored is not (DeribitCommandReceiptWriteResult.Inserted or DeribitCommandReceiptWriteResult.Duplicate)) throw new InvalidOperationException("Native receipt was not retained.");
                await new OmsPostgresJournal(source).RecordOutcomeAsync(scope,command.CommandId,OmsJournalDispatchOutcome.Acknowledged,ct);
                recovery.RequestRecovery();
                return new(OmsExecutionResultKind.Acknowledged,admission,"Native acknowledgement retained; fills and finality require recovery.");
            }
            catch
            {
                fence.Revoke(); transport.Abort(); recovery.RequestRecovery();
                if (started)
                {
                    using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await new OmsPostgresJournal(source).RecordOutcomeAsync(scope,command.CommandId,OmsJournalDispatchOutcome.Ambiguous,finish.Token);
                }
                return new(OmsExecutionResultKind.Unknown,admission,"Execution stopped; retained pending risk and recovery required. No resend.");
            }
        },ct);
    }

    private const string Where="tenant_id=@t AND venue_id=@v AND environment=@e AND account_id=@a AND subaccount_id=@s";
    private async Task<OmsExecutionResult> RejectScopeAsync(OmsJournalCommandRequest command,CancellationToken ct)
    {await OmsPostgresRiskAdmission.AuditUnavailableAsync(source,command,ct);return new(OmsExecutionResultKind.Denied,null,"Account scope mismatch.");}
    private async Task<OmsExecutionResult> RejectDispatchAsync(OmsJournalCommandRequest command,OmsLiveExecutionBaseline live,OmsRiskAdmissionResult admission,string reason,CancellationToken ct)
    {await using var db=await source.OpenConnectionAsync(ct);await OmsPostgresRiskAdmission.AuditAsync(db,null,command,live.Publication,live.Qualified,OmsRiskAdmissionResult.StaleBaseline,ct);return new(OmsExecutionResultKind.Denied,admission,reason);}
    private NpgsqlCommand Query(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        var q=new NpgsqlCommand(sql,db,tx); q.Parameters.AddWithValue("t",scope.Tenant.Value); q.Parameters.AddWithValue("v",scope.Venue.Value);
        q.Parameters.AddWithValue("e",(short)scope.Environment); q.Parameters.AddWithValue("a",scope.Account.Value); q.Parameters.AddWithValue("s",scope.Subaccount?.Value ?? ""); return q;
    }
    private static void BindLive(NpgsqlCommand q,OmsLiveExecutionBaseline live)
    { q.Parameters.AddWithValue("run",live.Publication.Token.Start.RunId); q.Parameters.AddWithValue("generation",live.Publication.Token.Generation); q.Parameters.AddWithValue("sha",live.Qualified.StateSha256); }
    private async Task<bool> LockBaseline(NpgsqlConnection db,NpgsqlTransaction tx,OmsLiveExecutionBaseline live,CancellationToken ct)
    {
        if (!live.Qualified.IsCurrent || recovery.Current!=live || live.Qualified.Start!=live.Publication.Token.Start) return false;
        await using var q=Query(db,tx,"SELECT public.oms_lock_risk_recovery_head_v1(@t,@v,@e,@a,@s)");
        return await q.ExecuteScalarAsync(ct) is Guid id && id==live.Publication.Token.Start.RunId && live.Qualified.IsCurrent;
    }
    public async ValueTask DisposeAsync()
    {
        fence.Revoke();recovery.RequestRecovery(); transport.Abort();
        // OS authority is released last, after both in-flight operation and transport have drained.
        await transport.DisposeAsync(); await fence.DisposeAsync();
    }
}
