using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitCrossPortfolioPostgresTests
{
    [Theory]
    [InlineData("valid")][InlineData("proof-currency")][InlineData("missing-settlement")][InlineData("long-outage")]
    public async Task ExplicitCollateralCoveragePublishesFullInventoryAndRejectsTamperingOrOldHistory(string mode)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);
        live.HistoryCoverage=DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2;
        if(mode=="long-outage")live.Mode=mode;
        var result=await live.Recover(mode=="valid");var doc=result.Document;
        Assert.Equal(2,doc.Candidate!.SchemaVersion);Assert.Equal(DeribitRecoveryCandidateScope.ListedDerivativeSettlementsV2,doc.Candidate.CoverageScope);
        Assert.Equal(4,doc.Candidate.CurrencyBalances!.Count);Assert.Equal(5,doc.CurrencyObservations!.Count);
        Assert.Equal(new[]{"BUIDL","MATIC"},doc.Universe!.HistoryCoverage!.UnqueriedCollateralCashCurrencies);
        Assert.Equal(DeribitRecoveryCoherencePolicy.CollateralPolicyV2,result.Coherence.Policy);
        if(mode=="valid")
        {
            Assert.NotNull(result.ExecutionBaseline);var saved=await live.Store.ReadPublishedAsync(Scope());Assert.NotNull(saved);
            Assert.Equal(4,saved.State.Balances.Count);Assert.Equal(DeribitRecoveryCoherencePolicy.CollateralPolicyV2,saved.State.Policy);
            Assert.True(await result.RuntimeGate!.CanUseRecoveredStateAsync());
            var ledger=await new OmsPostgresRecoveryLedgerReader(db.Source,true).ReadAsync(Scope());Assert.True(ledger.IsUsable);Assert.NotNull(ledger.RecoveryArchive);
            live.FromOverride=saved.State.NativeThroughUtc;
            var again=await live.Recover(true);Assert.NotNull(again.ExecutionBaseline);
            Assert.Equal(saved.State.RunId,again.Document.Ledger.RecoveryArchive!.SourceRunId);return;
        }
        if(mode=="long-outage"){Assert.Null(result.ExecutionBaseline);Assert.Contains(DeribitRecoveryCoherenceProblem.HistoryOutsideQualifiedRetention,result.Coherence.Problems);return;}
        doc=doc with{Coherence=null};
        if(mode=="proof-currency")doc=doc with{Universe=doc.Universe with{HistoryCoverage=doc.Universe.HistoryCoverage with{CollateralCurrencies=["ETH"]}}};
        else doc=doc with{CurrencyObservations=doc.CurrencyObservations.Take(4).ToArray()};
        var changed=DeribitRecoveryCoherencePolicy.Assess(doc);
        Assert.Null(changed.Publication);Assert.Contains(DeribitRecoveryCoherenceProblem.CurrencyCoverageUnavailable,changed.Assessment.Problems);
        Assert.Null(await live.Store.ReadPublishedAsync(Scope()));
    }
    [Theory]
    [InlineData("valid")][InlineData("expanded")][InlineData("no-proof")]
    public async Task VersionedPolicyIsHashedScopedAndDoesNotRelaxQualificationLimits(string mode)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var p=DeribitCrossPortfolioRiskTests.Policy();
        p=p with{Limits=p.Limits with{Scope=Scope()}};
        if(mode=="expanded")p=p with{Limits=p.Limits with{MaxOrderUsd=2}};
        if(mode=="no-proof")p=p with{CrossPortfolio=null};
        await OmsRiskPolicyAndRolePostgresTests.InsertPolicy(db,p,1);
        var reader=new OmsPostgresRiskPolicyReader(db.Source);
        if(mode!="valid"){await Assert.ThrowsAsync<InvalidOperationException>(()=>reader.ReadAsync(Scope()));return;}
        var read=await reader.ReadAsync(Scope());Assert.NotNull(read);Assert.Equal(2,read.SchemaVersion);
        Assert.Equal(OmsRecoveryRunCodec.Encode(p).Sha256,OmsRecoveryRunCodec.Encode(read).Sha256);
        Assert.Null(await reader.ReadAsync(Scope("another")));
    }

    [Fact]
    public async Task NativePmValuationIsDurablyAdmittedAndRemainsVisibleToPendingReader()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();var clock=new OmsRiskAdmissionPostgresTests.FrozenClock();await using var live=await Live.Open(db,clock);
        live.HistoryCoverage=DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2;live.CrossMargin=true;live.Cash=live.Equity=.004m;
        var recovered=await OmsExecutionGatewayPostgresTests.Prepare(live);var command=DeribitCrossPortfolioRiskTests.Command() with{Scope=Scope()};
        var port=new NativeInputs(clock);var admission=new OmsPostgresRiskAdmission(db.Source,port,clock);
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await admission.AdmitAsync(command,recovered.Current!.Publication,recovered.Current.Qualified));
        var pending=await new OmsPostgresPendingRiskReader(db.Source).ReadAsync(Scope(),new(Guid.NewGuid()));
        Assert.Equal(1,Assert.Single(pending).NativeUsd);
        await using var q=db.Source.CreateCommand("SELECT inputs_json::text,inputs_sha256 FROM oms_risk_reservations_v1");await using var rows=await q.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());
        var audit=OmsRecoveryRunCodec.Decode<OmsRiskInputsV1>(rows.GetString(0),rows.GetFieldValue<byte[]>(1));
        Assert.Equal(2,audit.Policy.SchemaVersion);Assert.Equal(3,audit.Valuation.SchemaVersion);Assert.Equal(3,audit.Valuation.CrossPortfolio!.ScenarioCount);
    }
    private sealed class NativeInputs(TimeProvider clock):IOmsRiskInputsReader
    {
        public Task<OmsRiskInputsV1?> ReadAsync(OmsJournalCommandRequest c,OmsRecoveredAccountStateV1 b,CancellationToken ct=default)
        {
            var at=clock.GetUtcNow();var input=DeribitCrossPortfolioRiskTests.Observation(b);
            var policy=DeribitCrossPortfolioRiskTests.Policy();policy=policy with{Limits=policy.Limits with{Scope=b.Scope},ValidFromUtc=at.AddMinutes(-1),ValidThroughUtc=at.AddHours(1)};
            input=input with{StartedUtc=at,CompletedUtc=at,LossFromUtc=new(at.UtcDateTime.Date,TimeSpan.Zero),LossThroughUtc=at,
                Before=input.Before with{ObservedUtc=at},After=input.After with{ObservedUtc=at},Scenarios=input.Scenarios.Select(x=>x with{Capital=x.Capital with{ObservedUtc=at}}).ToArray(),
                Instruments=input.Instruments.Select(x=>x with{ObservedUtc=at}).ToArray()};
            return Task.FromResult(DeribitCrossPortfolioRiskV2.Evaluate(c,b,policy,input,at));
        }
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task AddressedCancelAcceptsOnlyValidVersionedPmPolicy(bool valid)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);var(c,receipt,recovery)=await OmsAddressedCancelPostgresTests.Seed(db,live);
        var policies=new CancelPolicy(valid);var admission=new OmsPostgresRiskAdmission(db.Source,new OmsRiskAdmissionPostgresTests.Inputs(),cancelPolicies:policies);
        Assert.Equal(valid?OmsRiskAdmissionResult.Reserved:OmsRiskAdmissionResult.Denied,
            await admission.AdmitAsync(DeribitCommandReceiptFixtures.Cancel(c,receipt.Order.ExchangeOrderId),recovery.Current!.Publication,recovery.Current.Qualified));
    }
    private sealed class CancelPolicy(bool valid):IOmsRiskPolicyReader
    {
        public Task<OmsRiskPolicyDocumentV1?> ReadAsync(AccountKey s,CancellationToken ct=default)
        {
            var p=DeribitCrossPortfolioRiskTests.Policy();return Task.FromResult<OmsRiskPolicyDocumentV1?>(p with{Limits=p.Limits with{Scope=s},
                CrossPortfolio=valid?p.CrossPortfolio:null,ValidFromUtc=DateTimeOffset.UtcNow.AddMinutes(-1),ValidThroughUtc=DateTimeOffset.UtcNow.AddMinutes(1)});
        }
    }
}
