using System.Diagnostics;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.FixHostPreparation;

internal sealed record PmRiskInspectionResult(bool DiagnosticOnly,OmsRecoveredAccountStateV1 InventoryReference,
    DeribitCrossPmRiskObservationV2? Observation,long ObservationMilliseconds)
{public bool AccountReady=>false;public bool TradingReady=>false;public bool DatabaseUsed=>false;}

internal static class PmRiskInspection
{
    // A diagnostic reference assembled from actual native inventory and public units. It has no
    // coordinator revision, publication or physical permit, and cannot authorize any command.
    internal static async Task<PmRiskInspectionResult> CollectAsync(AccountInput input,HttpClient http,CancellationToken ct)
    {
        AccountInspection.ValidateInput(input,"READ_ONLY_FIX_PM_RISK_OBSERVATION_V2");var binding=AccountInspection.Binding(input);
        var catalog=await new DeribitInstrumentCatalog(http).ReadAsync(binding.Scope.Environment,"ETH","future",ct);
        var instruments=DeribitEthFutureAccountManifest.Create(catalog,binding.Scope.Environment,DateTimeOffset.UtcNow);
        var terms=DeribitRecoveryInstrumentManifest.Create(binding.Scope.Environment,[catalog],DateTimeOffset.UtcNow).ToDictionary(x=>x.NativeInstrument,StringComparer.Ordinal);
        binding=new(binding.Scope,binding.Options,DeribitPrivateCapturePlan.ForAccountRecovery(input.ExpectedAccountId,input.AccountType=="subaccount",instruments));
        var inventory=await new DeribitAccountInventoryReader(binding,http).ReadAsync(new(binding.Scope,Guid.NewGuid(),TimeSpan.FromSeconds(30)),ct);
        if(!inventory.QueriesCompleted||!inventory.IsFreshAt(DateTimeOffset.UtcNow)||inventory.OpenOrders.Count!=0)throw new InvalidOperationException("Unqualified diagnostic inventory.");
        var positions=new List<DeribitAccountPosition>();
        foreach(var row in inventory.Positions)
        {
            if(!terms.TryGetValue(row.NativeInstrument,out var m)||m.Kind!=row.Kind||m.AmountUnit!=OmsPrivateAmountUnit.Usd||m.AmountCurrency!="USD"||m.BaseCurrency!="ETH"||
                m.PositionSizeCurrency!="USD"||row.NativeBaseSize is null)throw new InvalidOperationException("Unqualified native position units.");
            positions.Add(new(row.NativeInstrument,row.Kind,row.Direction,row.NativeSize,m.PositionSizeCurrency,row.NativeBaseSize,m.BaseCurrency));
        }
        var quote=await new DeribitFixQualificationMarketV1(binding,http).ReadAsync(ct);var at=DateTimeOffset.UtcNow;
        var price=DeribitFixQualificationScenarioV1.Price(quote,at);var reference=new OmsRecoveredAccountStateV1(1,binding.Scope,inventory.Epoch,Guid.NewGuid(),"diagnostic-unqualified-v2",at,
            inventory.StartedAtUtc,inventory.CompletedAtUtc,[],[],positions,inventory.Balances,[],[],at.AddSeconds(5));
        var command=new OmsJournalCommandRequest(binding.Scope,new(Guid.NewGuid()),new(Guid.NewGuid()),new(Guid.NewGuid()),new(0),
            new(OmsJournalCommandKind.Place,"ETH-PERPETUAL",OrderSide.Buy,OrderType.Limit,1,OmsQuantityUnit.QuoteAsset,price,null,"GTC",false,true,
                "read-only-pm-risk-probe","diagnostic","no-execution-authority",at,"read-probe-"+Guid.NewGuid().ToString("N")));
        var timer=Stopwatch.StartNew();var native=await new DeribitCrossPortfolioRiskObservationReaderV2(binding,http,new NoPending()).ReadAsync(command,reference,ct);
        return new(true,reference,native,timer.ElapsedMilliseconds);
    }
    private sealed class NoPending:IOmsPendingRiskReader
    {public Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(AccountKey s,OmsCommandId except,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<OmsPendingNativeRiskV1>>([]);}
}
