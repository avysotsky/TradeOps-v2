using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryCurrencyBindingTests
{
    [Theory]
    [InlineData("scope")] [InlineData("time")] [InlineData("sources")]
    [InlineData("units")] [InlineData("channel")] [InlineData("collision")]
    public async Task MisboundCurrencyPortCannotArchiveItsFactsOrTurnExhaustionFlagsIntoCoverage(string mode)
    {
        var document = await Run(mode);
        Assert.Empty(document.CurrencyObservations!);
        Assert.Contains(document.Universe!.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable);
        Assert.Equal(DeribitRecoveryCandidateScope.Instrument, document.Candidate!.CoverageScope);
        Assert.DoesNotContain("foreign", JsonSerializer.Serialize(document)); Assert.False(document.Assessment.AccountReady);
    }

    [Fact]
    public async Task UnknownNativeReferenceIsRetainedWithoutFabricatingItsUnitsOrDiscardingTheGap()
    {
        var document = await Run("unmapped");
        var query = Assert.Single(document.CurrencyObservations!); Assert.Empty(query.Observations);
        Assert.Contains("NEW-NATIVE", document.Universe!.RequiredInstruments);
        Assert.Contains(document.Universe.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.UnmappedInstrument && x.NativeInstrument == "NEW-NATIVE");
        Assert.False(document.Universe.UniverseResolved); Assert.False(document.Candidate!.AccountReady);
    }

    private static async Task<DeribitRecoveryRunDocumentV1> Run(string mode)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var ports = new Ports(mode); var q = r.Request();
        var store = new Store();
        var engine = new DeribitRecoveryRunEngine(store, ports, r, r, new DeribitRecoveryAccountAssessmentTests.Accounts(),
            universeSources: new(ports, ports, [new("ETH-PERPETUAL", "ETH", "future", OmsPrivateAmountUnit.Usd, "USD", "ETH", "USD")]));
        var result = await engine.RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session);
        Assert.Same(result, store.Saved); return result;
    }

    private sealed class Ports(string mode) : IOmsRecoveryLedgerReader, IDeribitAccountInventoryReader, IDeribitCurrencyRecoveryQueries
    {
        public Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OmsRecoveryLedgerSnapshot(new(scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, [], []), [], []));
        public Task<DeribitAccountInventoryResult> ReadAsync(DeribitAccountInventoryRequest q, CancellationToken ct = default)
        {
            var at = DateTimeOffset.UtcNow;
            return Task.FromResult(new DeribitAccountInventoryResult(q.Scope, q.Epoch, at, at, q.MaximumObservationSpan,
                [new("ETH", 0, 0, false, false, new(at, at))], [], [], []));
        }
        public Task<DeribitCurrencyRecoveryResult> CollectAsync(DeribitCurrencyRecoveryQuery q, CancellationToken ct = default)
        {
            var at = DateTimeOffset.UtcNow;
            var order = new OmsPrivateVenueFacts(OmsJournalEventKind.Order, "ETH", "ETH-PERPETUAL", "native", OrderSide.Buy,
                1, OmsPrivateAmountUnit.Usd, "USD", q.FromUtc.AddSeconds(10), OrderState: "open", FilledNativeAmount: 0,
                PriceKind: OmsPrivatePriceKind.Numeric, Price: 2000);
            var envelope = new OmsPrivateEventEnvelope(q.Scope, order, new(Guid.NewGuid(), q.Epoch, at,
                OmsPrivateCaptureSource.RecoveryQuery, "private/get_open_orders_by_currency"));
            if (mode == "units") envelope = envelope with { Facts = order with { AmountUnit = OmsPrivateAmountUnit.BaseCoin, AmountCurrency = "ETH" } };
            if (mode == "channel") envelope = envelope with { Observation = envelope.Observation with { Channel = "private/get_order_state" } };
            var observations = new[] { envelope };
            if (mode == "collision")
            {
                envelope = envelope with { Facts = new(OmsJournalEventKind.Execution, "ETH", "ETH-PERPETUAL", "native", OrderSide.Buy,
                    1, OmsPrivateAmountUnit.Usd, "USD", q.FromUtc.AddSeconds(10), ExchangeTradeId: "trade", PriceKind: OmsPrivatePriceKind.Numeric,
                    Price: 2000, Fee: 0.0001m, FeeCurrency: "ETH"), Observation = envelope.Observation with { Channel = "private/get_user_trades_by_currency" } };
                observations = [envelope, envelope with { Facts = envelope.Facts with { Price = 2001 } }];
            }
            var sources = new[] { "open", mode == "sources" ? "open" : "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray();
            return Task.FromResult(new DeribitCurrencyRecoveryResult(mode == "scope" ? new(new("foreign"), Account.Venue, Account.Environment, Account.Account) : q.Scope,
                q.Epoch, q.Currency, q.Kind, DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), mode == "time" ? at.AddHours(-1) : at, at,
                sources, mode == "unmapped" ? [] : observations, mode == "unmapped" ? [DeribitCurrencyRecoveryProblem.UnmappedInstrument] : [],
                mode == "unmapped" ? ["NEW-NATIVE"] : []));
        }
    }
    private sealed class Store : IOmsRecoveryRunStore
    {
        internal DeribitRecoveryRunDocumentV1? Saved;
        public Task<OmsRecoveryRunToken> BeginAsync(OmsRecoveryRunStart start, CancellationToken ct = default) => Task.FromResult(new OmsRecoveryRunToken(start, 1));
        public Task MarkReconcilingAsync(OmsRecoveryRunToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteBlockedAsync(OmsRecoveryRunToken token, DeribitRecoveryRunDocumentV1 document, CancellationToken ct = default)
        { document.Validate(); Saved = document; return Task.CompletedTask; }
        public Task StopAsync(OmsRecoveryRunToken token, OmsRecoveryStopReason reason, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected failure instead of blocked assessment.");
        public Task<OmsRecoveryRunHead?> ReadHeadAsync(AccountKey scope, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DeribitRecoveryRunDocumentV1?> ReadResultAsync(AccountKey scope, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
