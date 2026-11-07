using System.Security.Cryptography;
using System.Text;
using QuickFix;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitQuickFixSessionTests
{
    internal sealed class MemoryStore:IOmsFixSessionStoreV1
    {
        internal readonly List<OmsFixStoredMessageV1> Rows=[];
        public DateTime CreationTimeUtc{get;}=DateTime.UtcNow;
        public ulong NextSender{get;private set;}=1;
        public ulong NextTarget{get;private set;}=1;
        private long logon;
        public void SaveAndAdvance(ulong sequence,string type,string? text)
        {Assert.Equal(NextSender,sequence);Rows.Add(new(sequence,type,text));NextSender++;}
        public void AdvanceSender(ulong next){if(next!=NextSender)throw new InvalidOperationException();}
        public void AdvanceTarget(ulong next){Assert.True(next>=NextTarget);NextTarget=next;}
        public IReadOnlyList<OmsFixStoredMessageV1> Read(ulong from,ulong through)=>Rows.Where(x=>x.Sequence>=from&&x.Sequence<=through).ToArray();
        public long ReserveLogonTimestamp(long now)=>logon=Math.Max(now,logon+1);
        public void Refresh(){}
        public void Dispose(){}
    }
    internal sealed class Rig:IDisposable
    {
        internal readonly MemoryStore Store;
        internal readonly OmsFixSessionKeyV1 Key=new(1,Scope(),"to-"+Guid.NewGuid().ToString("N"),"DERIBITSERVER");
        internal readonly List<Message> Sent=[];
        internal readonly List<string> Reports=[];
        internal int Recoveries,Aborts;
        internal bool Owner=true;
        internal readonly DeribitQuickFixSessionV1 Engine;
        internal Action? BeforePhysicalWrite;
        internal Rig(MemoryStore? store=null,OmsPhysicalSessionAuthority? actualAuthority=null,Func<OmsExternalEconomicIngress>? ingress=null)
        {
            Store=store??new();
            var authority=actualAuthority??new OmsPhysicalSessionAuthority(write=>Owner?write():throw new InvalidOperationException("Revoked owner."));
            Engine=new(new(Key),Store,authority,"synthetic-client","synthetic-secret",bytes=>
            {
                var m=Parse(Encoding.ASCII.GetString(bytes.Span));
                BeforePhysicalWrite?.Invoke();
                // The engine must commit the message and next sender BEFORE initiating physical I/O.
                if(!m.Header.IsSetField(43)||!m.Header.GetBoolean(43))Assert.True(Store.NextSender>m.Header.GetULong(34));
                Sent.Add(m);return Task.CompletedTask;
            },(raw,epoch,received)=>{Assert.Equal(Engine!.Epoch,epoch);Reports.Add(raw);},()=>Recoveries++,()=>Aborts++,ingress);
        }
        internal void Logon()
        {Engine.Tick();Engine.Receive(Encoding.ASCII.GetBytes(Peer("A",1,(98,"0"),(108,"30"),(141,"N"),(9001,"N"))));Assert.True(Engine.LoggedOn);}
        internal string Peer(string type,ulong sequence,params (int Tag,string Value)[] fields)
        {
            var m=new Message();m.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.4"));m.Header.SetField(new QuickFix.Fields.MsgType(type));
            m.Header.SetField(new QuickFix.Fields.SenderCompID("DERIBITSERVER"));m.Header.SetField(new QuickFix.Fields.TargetCompID(Key.SenderCompId));
            m.Header.SetField(new QuickFix.Fields.MsgSeqNum(sequence));m.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
            foreach(var(tag,value)in fields)m.SetField(new QuickFix.Fields.StringField(tag,value));return m.ConstructString();
        }
        public void Dispose()=>Engine.Dispose();
    }
    internal static Message Parse(string text){var m=new Message();m.FromString(text,false,null,null);return m;}
    [Fact]
    public async Task NativeSocketIngressInvalidatesBeforeWaitingForAnOccupiedEngineLock()
    {
        await using var setup=await OmsPrivateAccountRevisionTests.Setup.Open();var c=setup.Coordinator;
        var w=await c.BeginReplayAsync(setup.Session,setup.Epoch);var cut=await c.SealReplayAsync(w);
        var revision=c.TryCreateAccountRevision(cut,setup.Subscriptions)!;c.AbandonReplay(w);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r=new Rig(ingress:()=>{var token=c.BeginExternalIngress(setup.Session);observed.TrySetResult();return token;});
        r.BeforePhysicalWrite=()=>{entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();};
        var writer=Task.Run(()=>r.Engine.Tick());Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task receiver=Task.CompletedTask;
        try
        {
            receiver=Task.Run(()=>r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("A",1,(98,"0"),(108,"30"),(141,"N"),(9001,"N")))));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(receiver.IsCompleted);Assert.False(c.IsAccountRevisionCurrent(revision));
        }
        finally{release.Set();await Task.WhenAll(writer,receiver);}
    }

    [Fact]
    public async Task FragmentAndQueuedGapHoldExternalBarrierUntilCompleteDrain()
    {
        await using var setup=await OmsPrivateAccountRevisionTests.Setup.Open();var c=setup.Coordinator;
        using var r=new Rig(ingress:()=>c.BeginExternalIngress(setup.Session));r.Logon();
        var bytes=Encoding.ASCII.GetBytes(r.Peer("0",3));r.Engine.Receive(bytes.AsSpan(0,12));
        async Task<bool> Qualifies()
        {var w=await c.BeginReplayAsync(setup.Session,setup.Epoch);var cut=await c.SealReplayAsync(w);var ok=c.TryCreateAccountRevision(cut,setup.Subscriptions) is not null;c.AbandonReplay(w);return ok;}
        Assert.False(await Qualifies());r.Engine.Receive(bytes.AsSpan(12));Assert.True(r.Engine.HasGap);Assert.False(await Qualifies());
        r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("0",2)));Assert.False(r.Engine.HasGap);Assert.True(await Qualifies());
    }

    [Fact]
    public void NativeLogonSignatureUsesIncreasingTimestampDotBase64NonceAndSha256Concatenation()
    {
        var nonce=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();var signed=DeribitFixLogonV1.Sign(1793966400000,nonce,"synthetic-secret");
        Assert.Equal("1793966400000.AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",signed.RawData);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(signed.RawData+"synthetic-secret"))),signed.Password);
    }
    [Fact]
    public void EverySplitBoundaryAndCoalescedFramesPreserveExactFixMessages()
    {
        using var r=new Rig();var first=r.Peer("0",1);var second=r.Peer("1",2,(112,"probe"));var bytes=Encoding.ASCII.GetBytes(first+second);
        for(var split=0;split<=bytes.Length;split++)
        {var f=new DeribitFixFramerV1();var all=f.Add(bytes.AsSpan(0,split)).Concat(f.Add(bytes.AsSpan(split))).ToArray();Assert.Equal(new[]{first,second},all);f.Complete();}
    }
    [Theory]
    [InlineData("checksum")][InlineData("length")][InlineData("prefix")][InlineData("non-ascii")][InlineData("truncated")][InlineData("capacity")]
    public void CorruptTruncatedAndOversizedFramesAreRefused(string damage)
    {
        using var r=new Rig();var raw=r.Peer("0",1);var bytes=Encoding.ASCII.GetBytes(raw);
        if(damage=="checksum")bytes[^2]=(byte)(bytes[^2]=='0'?'1':'0');
        if(damage=="length")bytes[12]=(byte)(bytes[12]=='9'?'8':'9');
        if(damage=="prefix")bytes[2]=(byte)'B';
        if(damage=="non-ascii")bytes[20]=255;
        if(damage=="truncated")bytes=bytes[..^1];
        if(damage=="capacity")bytes=Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=32768\u000135=8\u0001");
        var f=new DeribitFixFramerV1();Assert.Throws<InvalidOperationException>(()=>{f.Add(bytes);f.Complete();});
    }
    [Fact]
    public void RealEngineLogonAndTestRequestPersistBeforeWriteAndNeverStoreAuthentication()
    {
        using var r=new Rig();r.Logon();var logon=r.Sent[0];Assert.Equal("A",logon.Header.GetString(35));Assert.Equal("synthetic-client",logon.GetString(553));
        Assert.False(logon.GetBoolean(9001));Assert.True(logon.GetBoolean(9015));Assert.True(logon.GetBoolean(9007));Assert.False(logon.GetBoolean(141));
        Assert.All(r.Store.Rows,x=>Assert.Null(x.ApplicationMessage));
        r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("1",2,(112,"check-live"))));
        Assert.Contains(r.Sent,x=>x.Header.GetString(35)=="0"&&x.IsSetField(112)&&x.GetString(112)=="check-live");Assert.Equal(3UL,r.Store.NextTarget);
    }
    [Fact]
    public void DirectEngineSendCannotBypassTheOmsPermitBoundary()
    {
        using var r=new Rig();r.Logon();var before=r.Store.NextSender;var raw=new Message();raw.Header.SetField(new QuickFix.Fields.MsgType("D"));
        raw.SetField(new QuickFix.Fields.ClOrdID("unqualified"));
        Assert.False(Session.SendToTarget(raw,new SessionID("FIX.4.4",r.Key.SenderCompId,"DERIBITSERVER")));
        Assert.Equal(before,r.Store.NextSender);Assert.DoesNotContain(r.Sent,x=>x.Header.GetString(35)=="D");
    }
    [Fact]
    public void ActualEngineDetectsGapRequestsResendAndDrainsQueuedSequenceOnce()
    {
        using var r=new Rig();r.Logon();r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("0",3)));
        Assert.True(r.Engine.HasGap);Assert.Equal(2UL,r.Store.NextTarget);Assert.True(r.Recoveries>0);
        Assert.Contains(r.Sent,x=>x.Header.GetString(35)=="2"&&x.GetULong(7)==2);
        r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("0",2)));Assert.False(r.Engine.HasGap);Assert.Equal(4UL,r.Store.NextTarget);
    }
    [Theory]
    [InlineData("D")][InlineData("F")][InlineData("G")]
    public void ReplayOfAnOldBusinessOrderUsesGapFillAndNeverReissuesIt(string type)
    {
        var store=new MemoryStore();store.SaveAndAdvance(1,"A",null);
        var old=new Message();old.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.4"));old.Header.SetField(new QuickFix.Fields.MsgType(type));
        old.Header.SetField(new QuickFix.Fields.MsgSeqNum(2UL));old.Header.SetField(new QuickFix.Fields.SenderCompID("old-owner"));old.Header.SetField(new QuickFix.Fields.TargetCompID("DERIBITSERVER"));
        old.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow.AddMinutes(-1)));old.SetField(new QuickFix.Fields.ClOrdID("possibly-sent"));
        store.SaveAndAdvance(2,type,old.ConstructString());
        using var r=new Rig(store);r.Logon();r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("2",2,(7,"1"),(16,"3"))));
        Assert.DoesNotContain(r.Sent,x=>x.Header.GetString(35) is "D" or "F" or "G");Assert.Contains(r.Sent,x=>x.Header.GetString(35)=="4"&&x.GetBoolean(123));
        Assert.Equal("possibly-sent",Parse(store.Rows[1].ApplicationMessage!).GetString(11));
    }
    [Theory]
    [InlineData("logon-reset")][InlineData("hard-reset")][InlineData("large-gap")]
    public void UnqualifiedResetOrUnboundedGapStopsEngineWithoutDiscardingPersistentState(string reason)
    {
        using var r=new Rig();r.Logon();var before=r.Store.NextTarget;
        var frame=reason switch{"logon-reset"=>r.Peer("A",2,(98,"0"),(108,"30"),(141,"Y")),"hard-reset"=>r.Peer("4",2,(36,"1")),_=>r.Peer("0",100)};
        Assert.Throws<InvalidOperationException>(()=>r.Engine.Receive(Encoding.ASCII.GetBytes(frame)));Assert.False(r.Engine.LoggedOn);
        Assert.Equal(before,r.Store.NextTarget);Assert.Equal(1,r.Aborts);Assert.True(r.Recoveries>0);
    }
    [Fact]
    public void RevokedPhysicalOwnerCannotEmitEvenAnEngineHeartbeat()
    {
        using var r=new Rig();r.Logon();var count=r.Sent.Count;r.Owner=false;
        Assert.Throws<InvalidOperationException>(()=>r.Engine.Receive(Encoding.ASCII.GetBytes(r.Peer("1",2,(112,"paused-owner")))));
        Assert.Equal(count,r.Sent.Count);Assert.False(r.Engine.LoggedOn);
    }
}
