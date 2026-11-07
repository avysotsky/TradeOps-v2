using QuickFix;

namespace TradeOps.Connectors.Deribit.Fix;

/// <summary>Independent last-mile allowlist for the nontrading connection probe. A permit,
/// stored resend, engine change or caller cannot make this wire transmit a business message.</summary>
internal sealed class DeribitFixAdminOnlyWireV1(IDeribitFixWireV1 inner,int maximumFrameBytes,Action<string>? initiating=null):IDeribitFixWireV1
{
    private int writes,blocked,logons,tests,logouts;
    internal int BusinessBlocked=>Volatile.Read(ref blocked);
    internal int Logons=>Volatile.Read(ref logons);
    internal int TestRequests=>Volatile.Read(ref tests);
    internal int Logouts=>Volatile.Read(ref logouts);
    public async Task WriteAsync(ReadOnlyMemory<byte> frame,CancellationToken ct)
    {
        if(frame.Length is <16||frame.Length>maximumFrameBytes)throw new InvalidOperationException("Bounded nontrading frame required.");
        var parser=new DeribitFixFramerV1(maximumFrameBytes);var frames=parser.Add(frame.Span);
        if(frames.Count!=1||parser.HasPending)throw new InvalidOperationException("Exactly one complete nontrading frame required.");
        var message=new Message();message.FromString(frames[0],false,null,null);var type=message.Header.GetString(35);
        if(type is not("A" or "0" or "1" or "2" or "4" or "5"))
        {Interlocked.Increment(ref blocked);throw new InvalidOperationException("Nontrading wire refuses all application messages.");}
        if(Interlocked.Increment(ref writes)>64)throw new InvalidOperationException("Nontrading admin capacity exceeded.");
        initiating?.Invoke(type);await inner.WriteAsync(frame,ct);
        if(type=="A")Interlocked.Increment(ref logons);
        if(type=="1")Interlocked.Increment(ref tests);
        if(type=="5")Interlocked.Increment(ref logouts);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct)=>inner.ReadAsync(buffer,ct);
    public void Abort()=>inner.Abort();
    public ValueTask DisposeAsync()=>inner.DisposeAsync();
}
