using System.Diagnostics;

namespace TradeOps.Connectors.Deribit;

// One observation, at most four outstanding reads, starts spaced by 150ms, no retries.
// A single shared RPC also retains its existing total response-byte ceiling.
internal sealed class DeribitRecoveryReadBudgetV2 : IDisposable
{
    private readonly SemaphoreSlim outstanding=new(4,4), launches=new(1,1);
    private long last;
    private int count;
    internal async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await outstanding.WaitAsync(ct);
        try
        {
            await launches.WaitAsync(ct);
            try
            {
                if(++count>64)throw new InvalidOperationException("Read observation capacity exceeded.");
                if(last!=0)
                {
                    var remaining=TimeSpan.FromMilliseconds(150)-Stopwatch.GetElapsedTime(last);
                    if(remaining>TimeSpan.Zero)await Task.Delay(remaining,ct);
                }
                last=Stopwatch.GetTimestamp();return new Lease(outstanding);
            }
            finally{launches.Release();}
        }
        catch{outstanding.Release();throw;}
    }
    private sealed class Lease(SemaphoreSlim gate):IDisposable
    {private int ended;public void Dispose(){if(Interlocked.Exchange(ref ended,1)==0)gate.Release();}}
    public void Dispose(){outstanding.Dispose();launches.Dispose();}
}
