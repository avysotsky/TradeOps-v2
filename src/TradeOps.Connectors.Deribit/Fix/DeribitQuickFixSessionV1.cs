using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuickFix;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

/// <summary>Actual QuickFIX/n session with an immediate owned responder. No engine socket queue,
/// reconnect thread, raw logger, automatic reset, or business-message replay.</summary>
internal sealed class DeribitQuickFixSessionV1:IApplicationExt,IResponder,IDisposable
{
    private readonly object sync=new();
    private readonly DeribitFixOptionsV1 options;
    private readonly IOmsFixSessionStoreV1 store;
    private readonly OmsPhysicalSessionAuthority authority;
    private readonly Func<ReadOnlyMemory<byte>,Task> physicalWrite;
    private readonly Action<string,Guid,DateTimeOffset> report;
    private readonly Action recoveryRequired;
    private readonly Action abort;
    private readonly string clientId,secret;
    private readonly SessionID id;
    private readonly OwnedInitiator initiator;
    private readonly Session session;
    private readonly HashSet<ulong> queued=[];
    private readonly DeribitFixFramerV1 framer;
    private readonly Func<OmsExternalEconomicIngress>? beginIngress;
    private readonly List<OmsExternalEconomicIngress> ingress = [];
    private OmsJournalCommandRequest? sending;
    private OmsPhysicalWritePermit? permit;
    private Message? expectedMessage;
    private int stopped;
    private bool disposed;
    internal Guid Epoch{get;}=Guid.NewGuid();
    internal bool LoggedOn=>Volatile.Read(ref stopped)==0&&session.IsLoggedOn;
    internal bool HasGap{get{lock(sync)return queued.Count!=0;}}

    internal DeribitQuickFixSessionV1(DeribitFixOptionsV1 options,IOmsFixSessionStoreV1 store,
        OmsPhysicalSessionAuthority authority,string clientId,string secret,Func<ReadOnlyMemory<byte>,Task> physicalWrite,
        Action<string,Guid,DateTimeOffset> report,Action recoveryRequired,Action abort,
        Func<OmsExternalEconomicIngress>? beginIngress=null)
    {
        options.Validate();
        if(string.IsNullOrWhiteSpace(clientId)||clientId.Length>512||clientId.Any(c=>c<33||c>126)||
            string.IsNullOrWhiteSpace(secret)||secret.Length>4096||secret.Any(char.IsControl))throw new ArgumentException("FIX credentials unavailable.");
        this.options=options;this.store=store;this.authority=authority;this.clientId=clientId;this.secret=secret;
        this.physicalWrite=physicalWrite;this.report=report;this.recoveryRequired=recoveryRequired;this.abort=abort;
        this.beginIngress=beginIngress;
        framer=new(options.MaximumFrameBytes);id=new("FIX.4.4",options.Session.SenderCompId,options.Session.TargetCompId);
        if(Session.LookupSession(id) is not null)throw new InvalidOperationException("FIX session identity already active in this process.");
        var path=Path.Combine(AppContext.BaseDirectory,"Fix","DeribitFix44ProfileV1.xml");
        using(var resource=typeof(DeribitQuickFixSessionV1).Assembly.GetManifestResourceStream("TradeOps.Connectors.Deribit.Fix.DeribitFix44ProfileV1.xml")??throw new InvalidOperationException("FIX dictionary unavailable."))
        {
            using var compiled=new MemoryStream();resource.CopyTo(compiled);
            static byte[] ProfileHash(byte[] bytes)=>SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n","\n",StringComparison.Ordinal)));
            if(!CryptographicOperations.FixedTimeEquals(ProfileHash(compiled.ToArray()),ProfileHash(File.ReadAllBytes(path))))
                throw new InvalidOperationException("FIX dictionary differs from the compiled profile.");
        }
        var settings=new SessionSettings();var values=new SettingsDictionary();
        foreach(var(k,v)in new Dictionary<string,string>{["ConnectionType"]="initiator",["BeginString"]="FIX.4.4",
            ["SenderCompID"]=id.SenderCompID,["TargetCompID"]=id.TargetCompID,["HeartBtInt"]=options.HeartbeatSeconds.ToString(CultureInfo.InvariantCulture),
            ["UseDataDictionary"]="Y",["DataDictionary"]=path,["NonStopSession"]="Y",["ResetOnLogon"]="N",["ResetOnLogout"]="N",
            ["ResetOnDisconnect"]="N",["RefreshOnLogon"]="N",["PersistMessages"]="Y",["ValidateLengthAndChecksum"]="Y",
            ["ValidateUserDefinedFields"]="Y",["AllowUnknownMsgFields"]="N",["CheckLatency"]="Y",["MaxLatency"]="5",
            ["LogonTimeout"]="10",["LogoutTimeout"]="2",["MaxMessagesInResendRequest"]="32",["Encoding"]="iso-8859-1"})values.SetString(k,v);
        settings.Set(id,values);
        initiator=new(this,new QuickFixDurableStoreFactoryV1(id,store),settings);
        try
        {
            initiator.Start();session=Session.LookupSession(id)??throw new InvalidOperationException("FIX session was not created.");
            session.RedactFieldsInLogs=[96,553,554,9005];session.SetResponder(this);session.Logon();
        }
        catch{initiator.Stop(true);initiator.Dispose();Session.LookupSession(id)?.Dispose();throw;}
    }

    internal void Tick()
    {lock(sync){EnsureRunning();try{session.Next();}catch{Fail();throw;}}}
    internal void Receive(ReadOnlySpan<byte> bytes)
    {
        // Must precede engine sync: a writer can own that lock while still awaiting I/O.
        var ticket=bytes.IsEmpty?null:beginIngress?.Invoke();
        lock(sync)
        {
            if(ticket is not null)ingress.Add(ticket);
            try
            {
                EnsureRunning();
                foreach(var frame in framer.Add(bytes))
                {
                    // QuickFIX may discard an old PossDup without calling FromApp. Native
                    // journal validation must still detect changed economic payloads.
                    var native=new Message();native.FromString(frame,false,null,null);
                    if(native.Header.GetULong(34)<store.NextTarget && native.Header.GetString(35) is "8" or "9")
                    {recoveryRequired();report(frame,Epoch,DateTimeOffset.UtcNow);}
                    session.Next(frame);queued.RemoveWhere(x=>x<session.NextTargetMsgSeqNum);EnsureRunning();
                }
                if(!framer.HasPending&&queued.Count==0)
                {foreach(var pending in ingress)pending.Complete();ingress.Clear();}
            }
            catch{foreach(var pending in ingress)pending.Dispose();ingress.Clear();Fail();throw new InvalidOperationException("FIX receive stopped; recovery required.");}
        }
    }
    internal void SendCommand(OmsJournalCommandRequest command,OmsPhysicalWritePermit grant,DeribitFixInstrumentV1 metadata,
        DeribitFixCancelBindingV1? parent=null)
    {
        lock(sync)
        {
            EnsureRunning();if(!session.IsLoggedOn||queued.Count!=0)throw new InvalidOperationException("FIX session is not coherent.");
            var message=DeribitFixCommandEncoderV1.Encode(command,options.Session,metadata,DateTimeOffset.UtcNow,parent);
            sending=command;permit=grant;expectedMessage=message;
            try{if(!session.Send(message))throw new InvalidOperationException("FIX physical write not confirmed.");}
            catch{Fail();throw;}
            finally{sending=null;permit=null;expectedMessage=null;}
        }
    }
    public bool Send(string text)
    {
        EnsureRunning();var message=new Message();message.FromString(text,false,null,null);
        var type=message.Header.GetString(35);
        if(type=="3"||type=="j"){Fail();return false;}
        try
        {
            authority.InitiateAsync(()=>
            {
                var bytes=Encoding.ASCII.GetBytes(text);
                if(Message.IsAdminMsgType(type))return physicalWrite(bytes);
                if(sending is null||permit is null||type is not("D" or "F")||message.Header.IsSetField(43)&&message.Header.GetBoolean(43))
                    throw new InvalidOperationException("Unqualified FIX business write refused.");
                return permit.InitiateAsync(sending,()=>physicalWrite(bytes));
            }).GetAwaiter().GetResult();
            return true;
        }
        catch{Fail();return false;}
    }
    public void ToAdmin(Message message,SessionID sessionId)
    {
        EnsureRunning();if(message.Header.GetString(35)!="A")return;
        var timestamp=store.ReserveLogonTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var auth=DeribitFixLogonV1.Sign(timestamp,RandomNumberGenerator.GetBytes(32),secret);
        message.SetField(new QuickFix.Fields.RawDataLength(auth.RawData.Length));message.SetField(new QuickFix.Fields.RawData(auth.RawData));
        message.SetField(new QuickFix.Fields.Username(clientId));message.SetField(new QuickFix.Fields.Password(auth.Password));
        message.SetField(new QuickFix.Fields.ResetSeqNumFlag(false));
        foreach(var(tag,value)in new[]{(9001,"N"),(9002,"N"),(9007,"Y"),(9009,"N"),(9010,"N"),(9015,"Y")})message.SetField(new QuickFix.Fields.StringField(tag,value));
    }
    public void ToApp(Message message,SessionID sessionId)
    {
        if(message.Header.IsSetField(43)&&message.Header.GetBoolean(43))throw new DoNotSend();
        if(Volatile.Read(ref stopped)!=0||sending is null||permit is null||!ReferenceEquals(message,expectedMessage))throw new DoNotSend();
    }
    public void FromEarlyIntercept(Message message,SessionID sessionId)
    {
        EnsureRunning();var type=message.Header.GetString(35);var sequence=message.Header.GetULong(34);var next=store.NextTarget;
        if(sequence==0||sequence>next+32)throw new InvalidOperationException("FIX receive gap capacity exceeded.");
        if(sequence>next){queued.Add(sequence);recoveryRequired();}
        if(type is "8" or "9")recoveryRequired();
        if(type=="A"&&message.IsSetField(141)&&message.GetBoolean(141))throw new InvalidOperationException("Peer sequence reset refused.");
        if(type=="4")
        {
            recoveryRequired();
            if(!message.IsSetField(123)||!message.GetBoolean(123)||message.GetULong(36)<next||message.GetULong(36)>next+10000)
                throw new InvalidOperationException("Unqualified FIX sequence reset.");
        }
    }
    public void FromAdmin(Message message,SessionID sessionId)
    {
        if(message.Header.GetString(35)=="A")
        {
            if(message.GetInt(108)!=options.HeartbeatSeconds||message.IsSetField(553)&&message.GetString(553)!=clientId||
                message.IsSetField(9001)&&message.GetBoolean(9001))throw new RejectLogon("Unexpected FIX logon profile.");
        }
        else if(message.Header.GetString(35)=="3"){Fail();throw new InvalidOperationException("FIX session reject.");}
    }
    public void FromApp(Message message,SessionID sessionId)
    {
        if(message.Header.GetString(35) is not("8" or "9")){Fail();throw new InvalidOperationException("Unsupported FIX application message.");}
        try{report(message.ConstructString(),Epoch,DateTimeOffset.UtcNow);}
        catch{Fail();throw new InvalidOperationException("FIX report processing stopped; recovery required.");}
    }
    public void OnCreate(SessionID sessionId){}
    public void OnLogon(SessionID sessionId){}
    public void OnLogout(SessionID sessionId)=>Fail();
    public void Disconnect()=>Fail();
    private void EnsureRunning(){if(Volatile.Read(ref stopped)!=0)throw new InvalidOperationException("FIX session stopped.");}
    private void Fail()
    {if(Interlocked.Exchange(ref stopped,1)==0){try{recoveryRequired();}finally{abort();}}}
    public void Dispose()
    {
        Fail();lock(sync){if(disposed)return;disposed=true;foreach(var pending in ingress)pending.Dispose();ingress.Clear();initiator.Stop(true);session.Dispose();initiator.Dispose();}
    }
    private sealed class OwnedInitiator(IApplication app,QuickFix.Store.IMessageStoreFactory stores,SessionSettings settings)
        :AbstractInitiator(app,stores,settings,Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,new QuickFix.FIX44.MessageFactory())
    {
        protected override void OnStart(){}
        protected override bool OnPoll(double timeout)=>false;
        protected override void OnStop(){}
        protected override void DoConnect(Session session,SettingsDictionary settings)=>throw new InvalidOperationException("Engine-managed sockets refused.");
    }
}
