using QuickFix;
using QuickFix.Store;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

internal sealed class QuickFixDurableStoreV1(IOmsFixSessionStoreV1 store):IMessageStore
{
    public ulong NextSenderMsgSeqNum{get=>store.NextSender;set=>store.AdvanceSender(value);}
    public ulong NextTargetMsgSeqNum{get=>store.NextTarget;set=>store.AdvanceTarget(value);}
    public DateTime? CreationTime=>store.CreationTimeUtc;
    public void IncrNextSenderMsgSeqNum()=>store.AdvanceSender(checked(store.NextSender+1));
    public void IncrNextTargetMsgSeqNum()=>store.AdvanceTarget(checked(store.NextTarget+1));
    public bool SetAndIncrNextSenderMsgSeqNum(ulong sequence,string text)
    {
        var message=new Message();message.FromString(text,false,null,null);
        if(message.Header.GetULong(34)!=sequence)throw new InvalidOperationException("FIX store sequence mismatch.");
        var type=message.Header.GetString(35);
        // Session-level replay uses gap fills. Authentication material never reaches persistence.
        store.SaveAndAdvance(sequence,type,Message.IsAdminMsgType(type)?null:text);return true;
    }
    public bool Set(ulong sequence,string message)=>throw new InvalidOperationException("Non-atomic FIX persistence refused.");
    public void Get(ulong from,ulong through,List<string> messages)
    {foreach(var row in store.Read(from,through))if(row.ApplicationMessage is{} text)messages.Add(text);}
    public void Reset()=>throw new InvalidOperationException("Automatic FIX sequence reset refused; preserve the journal.");
    public void Refresh()=>store.Refresh();
    public void Dispose()=>store.Dispose();
}

internal sealed class QuickFixDurableStoreFactoryV1(SessionID expected,IOmsFixSessionStoreV1 store):IMessageStoreFactory
{
    private int created;
    public IMessageStore Create(SessionID session)
    {if(!session.Equals(expected)||Interlocked.Exchange(ref created,1)!=0)throw new InvalidOperationException("Unexpected FIX store session.");return new QuickFixDurableStoreV1(store);}
}
