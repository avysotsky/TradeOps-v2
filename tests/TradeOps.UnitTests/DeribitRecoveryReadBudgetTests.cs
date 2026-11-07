using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitRecoveryReadBudgetTests
{
    [Fact]
    public async Task ConcurrentReadsHaveUniqueIdsBoundedOutstandingAndSpacedStarts()
    {
        using var handler=new Venue(false);using var http=new HttpClient(handler);using var budget=new DeribitRecoveryReadBudgetV2();
        var rpc=new DeribitRecoveryRpc(http,OmsEnvironment.Testnet,budget);
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>rpc.Call("public/get_time",new{},default)));
        Assert.Equal(8,handler.Ids.Distinct().Count());Assert.InRange(handler.Maximum,2,4);Assert.Equal(0,handler.Active);
        var starts=handler.Starts.Order().ToArray();Assert.Equal(8,starts.Length);
        Assert.All(starts.Zip(starts.Skip(1)),p=>Assert.True(Stopwatch.GetElapsedTime(p.First,p.Second)>=TimeSpan.FromMilliseconds(125)));
    }
    [Fact]
    public async Task ParallelResponsesShareTheSameEightMegabyteCeiling()
    {
        using var handler=new Venue(true);using var http=new HttpClient(handler);using var budget=new DeribitRecoveryReadBudgetV2();
        var rpc=new DeribitRecoveryRpc(http,OmsEnvironment.Testnet,budget);
        var outcomes=await Task.WhenAll(Enumerable.Range(0,18).Select(async _=>
        {try{await rpc.Call("public/get_time",new{},default);return false;}catch(DeribitRecoveryQueryException e){Assert.Equal(TradeOps.Application.Oms.DeribitRecoveryQueryProblem.Capacity,e.Problem);return true;}}));
        Assert.Contains(true,outcomes);Assert.Equal(0,handler.Active);Assert.Equal(18,handler.Ids.Distinct().Count());
    }
    private sealed class Venue(bool large):HttpMessageHandler
    {
        internal readonly ConcurrentBag<long> Ids=[],Starts=[];internal int Active,Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Starts.Add(Stopwatch.GetTimestamp());var n=Interlocked.Increment(ref Active);int current;
            do{current=Maximum;if(current>=n)break;}while(Interlocked.CompareExchange(ref Maximum,n,current)!=current);
            try
            {
                using var parsed=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var id=parsed.RootElement.GetProperty("id").GetInt64();Ids.Add(id);
                await Task.Delay(large?1:500,ct);
                return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id,result=large?new string('x',500000):"ok"}))};
            }
            finally{Interlocked.Decrement(ref Active);}
        }
    }
}
