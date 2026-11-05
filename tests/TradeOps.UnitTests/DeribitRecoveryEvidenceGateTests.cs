using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryEvidenceGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static DeribitRecoveryGateRequest Request() => new(new(Account, Epoch, "ETH-PERPETUAL",
        Now.AddHours(-2), Now.AddSeconds(-10), TimeSpan.FromMinutes(2)), "ETH", OmsPrivateAmountUnit.Usd, "USD");
    private static OmsPrivateEventEnvelope Seed(bool expired = false) => Report(expired ? "cancelled" : "open") with {
        Facts = Report(expired ? "cancelled" : "open").Facts with { VenueTimestampUtc = Now.AddMinutes(expired ? -31 : -1) },
        Observation = new(Guid.NewGuid(), Guid.NewGuid(), Now.AddSeconds(-30), OmsPrivateCaptureSource.WebSocket, "orders") };
    private static OmsPrivateEventEnvelope Current(OmsPrivateEventEnvelope seed, string method = "private/get_order_state") => seed with {
        Facts = seed.Facts with { VenueTimestampUtc = Now.AddSeconds(-20) },
        Observation = new(Guid.NewGuid(), Epoch, Now, OmsPrivateCaptureSource.RecoveryQuery, method) };

    [Fact]
    public async Task FoundKnownOrderDoesNotGrantCoherenceOrPublication()
    {
        var p = new Ports(); var result = await p.Gate().AssessAsync(Request());
        Assert.False(result.CanRebuildOrders); Assert.False(result.AccountReady); Assert.False(result.DurableProjectionPublished);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.HistoricalUnfilledCoverageGap, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Blockers);
        Assert.Equal(1, result.AddressedOrders); Assert.Equal(1, result.RetainedFacts); Assert.Equal(1, p.KnownCalls);
    }

    [Fact]
    public async Task ExpiredTerminalReferenceBlocksWithoutRepeatingUnavailableLookup()
    {
        var p = new Ports(expired: true); var result = await p.Gate().AssessAsync(Request());
        Assert.Equal(1, result.RetentionExpiredOrders); Assert.Equal(0, result.AddressedOrders); Assert.Equal(0, p.KnownCalls);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.KnownOrderRetentionExpired, result.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable, result.Blockers);
        Assert.Equal("cancelled", p.Snapshot.Observations[0].Envelope.Facts.OrderState);
    }

    [Fact]
    public async Task CurrentReportAvoidsRedundantAddressedLookupEvenForOldTerminalSeed()
    {
        var p = new Ports(expired: true); p.Batch = p.Batch with {
            Observations = new[] { Current(p.Snapshot.Observations[0].Envelope, "private/get_order_history_by_instrument") } };
        var result = await p.Gate().AssessAsync(Request());
        Assert.Equal(0, p.KnownCalls); Assert.Equal(0, result.RetentionExpiredOrders);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable, result.Blockers);
        Assert.False(result.CanRebuildOrders);
    }

    [Fact]
    public async Task OldOpenReferenceStillGetsAddressedBecauseItMayRemainOpen()
    {
        var p = new Ports(expired: true); var row = p.Snapshot.Observations[0];
        p.Snapshot = p.Snapshot with { Observations = new[] { row with { Envelope = row.Envelope with { Facts = row.Envelope.Facts with { OrderState = "open" } } } } };
        var result = await p.Gate().AssessAsync(Request());
        Assert.Equal(1, p.KnownCalls); Assert.Equal(0, result.RetentionExpiredOrders);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.KnownOrderRetentionExpired, result.Blockers);
    }

    [Fact]
    public async Task QuietEmptyRetainedScopeDoesNotInventKnownOrdersOrReadiness()
    {
        var p = new Ports(); p.Snapshot = p.Snapshot with { AcceptedFactCount = 0, Observations = Array.Empty<OmsPrivateRecoveryObservation>() };
        var result = await p.Gate().AssessAsync(Request());
        Assert.Equal(0, result.KnownOrders); Assert.Equal(0, p.KnownCalls); Assert.Equal(1, p.QueryCalls); Assert.False(result.CanRebuildOrders);
    }

    [Theory]
    [InlineData("incident", DeribitRecoveryEvidenceBlocker.SnapshotUnusable)]
    [InlineData("scope", DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid)]
    [InlineData("snapshot", DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid)]
    [InlineData("cached", DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid)]
    [InlineData("count", DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid)]
    [InlineData("embedded", DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid)]
    [InlineData("disposition", DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid)]
    public async Task InvalidSnapshotNeverContactsVenue(string mode, DeribitRecoveryEvidenceBlocker blocker)
    {
        var p = new Ports(); var row = p.Snapshot.Observations[0];
        p.Snapshot = mode switch {
            "incident" => p.Snapshot with { Problems = new[] { OmsPrivateRecoveryReadProblem.IntegrityIncident } },
            "scope" => p.Snapshot with { Scope = Foreign() }, "snapshot" => p.Snapshot with { SnapshotId = Guid.Empty },
            "cached" => p.Snapshot with { ReadAtUtc = Now.AddMinutes(-1) }, "count" => p.Snapshot with { AcceptedFactCount = 0 },
            "embedded" => p.Snapshot with { Observations = new[] { row with { Envelope = row.Envelope with { Scope = Foreign() } } } },
            _ => p.Snapshot with { Observations = new[] { row with { Disposition = OmsPrivateCaptureResult.Collision } } } };
        var result = await p.Gate().AssessAsync(Request());
        Assert.Contains(blocker, result.Blockers); Assert.Equal(0, p.QueryCalls); Assert.Equal(0, p.KnownCalls);
    }

    [Theory]
    [InlineData("scope", DeribitRecoveryEvidenceBlocker.QueryBindingInvalid)]
    [InlineData("epoch", DeribitRecoveryEvidenceBlocker.QueryBindingInvalid)]
    [InlineData("currency", DeribitRecoveryEvidenceBlocker.QueryBindingInvalid)]
    [InlineData("cached", DeribitRecoveryEvidenceBlocker.QueryBindingInvalid)]
    [InlineData("window", DeribitRecoveryEvidenceBlocker.QueryWindowInvalid)]
    [InlineData("failed", DeribitRecoveryEvidenceBlocker.QueryFailed)]
    [InlineData("source", DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid)]
    [InlineData("sources-complete-count-only", DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid)]
    [InlineData("truncated", DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid)]
    [InlineData("envelope-epoch", DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid)]
    [InlineData("envelope-scope", DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid)]
    [InlineData("kind-channel", DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid)]
    [InlineData("units", DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid)]
    [InlineData("contradictory-orders", DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid)]
    public async Task InvalidQueryEvidenceDoesNotTriggerAddressedFallback(string mode, DeribitRecoveryEvidenceBlocker blocker)
    {
        var p = new Ports(); var current = Current(p.Snapshot.Observations[0].Envelope, "private/get_order_history_by_instrument");
        p.Batch = mode switch {
            "scope" => p.Batch with { Scope = Foreign() }, "epoch" => p.Batch with { Epoch = Guid.NewGuid() },
            "currency" => p.Batch with { Currency = "BTC" }, "cached" => p.Batch with { StartedAtUtc = Now.AddSeconds(-1) },
            "window" => p.Batch with { QueryFromUtc = Now },
            "failed" => p.Batch with { Problems = new[] { DeribitRecoveryQueryProblem.Capacity } },
            "source" => p.Batch with { Sources = p.Batch.Sources.Take(4).ToArray() },
            "sources-complete-count-only" => p.Batch with { Sources = Enumerable.Repeat(p.Batch.Sources[0], 5).ToArray() },
            "truncated" => p.Batch with { Sources = p.Batch.Sources.Select(x => x with { Exhausted = false }).ToArray() },
            "envelope-epoch" => p.Batch with { Observations = new[] { current with { Observation = current.Observation with { StreamEpoch = Guid.NewGuid() } } } },
            "envelope-scope" => p.Batch with { Observations = new[] { current with { Scope = Foreign() } } },
            "kind-channel" => p.Batch with { Observations = new[] { current with { Observation = current.Observation with { Channel = "private/get_user_trades_by_instrument" } } } },
            "units" => p.Batch with { Observations = new[] { current with { Facts = current.Facts with { Currency = "BTC" } } } },
            _ => p.Batch with { Observations = new[] { current, current with { Facts = current.Facts with { Price = 2001 } } } } };
        var result = await p.Gate().AssessAsync(Request());
        Assert.Contains(blocker, result.Blockers); Assert.Equal(0, p.KnownCalls);
    }

    [Theory]
    [InlineData("scope", DeribitRecoveryEvidenceBlocker.KnownReadBindingInvalid)]
    [InlineData("epoch", DeribitRecoveryEvidenceBlocker.KnownReadBindingInvalid)]
    [InlineData("snapshot", DeribitRecoveryEvidenceBlocker.KnownReadBindingInvalid)]
    [InlineData("index", DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid)]
    [InlineData("missing", DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable)]
    [InlineData("missing-with-facts", DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid)]
    [InlineData("foreign-report", DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid)]
    [InlineData("failed", DeribitRecoveryEvidenceBlocker.KnownReadFailed)]
    public async Task KnownReadCannotSubstituteBindingsOrHideUnavailableOrders(string mode, DeribitRecoveryEvidenceBlocker blocker)
    {
        var p = new Ports();
        p.Known = mode switch {
            "scope" => p.Known with { Scope = Foreign() }, "epoch" => p.Known with { Epoch = Guid.NewGuid() },
            "snapshot" => p.Known with { SnapshotId = Guid.NewGuid() },
            "index" => p.Known with { Lookups = new[] { new DeribitKnownOrderLookup(5, DeribitKnownOrderLookupOutcome.Found) } },
            "missing" => p.Known with { Lookups = new[] { new DeribitKnownOrderLookup(0, DeribitKnownOrderLookupOutcome.Missing, 10004) }, Observations = Array.Empty<OmsPrivateEventEnvelope>() },
            "missing-with-facts" => p.Known with { Lookups = new[] { new DeribitKnownOrderLookup(0, DeribitKnownOrderLookupOutcome.Missing, 10004) } },
            "foreign-report" => p.Known with { Observations = new[] { p.Known.Observations[0] with { Scope = Foreign() } } },
            _ => p.Known with { Problems = new[] { DeribitKnownOrderReadProblem.Protocol } } };
        var result = await p.Gate().AssessAsync(Request()); Assert.Contains(blocker, result.Blockers); Assert.False(result.CanRebuildOrders);
    }

    [Fact]
    public async Task ExternalOrderRemainsBlockingAndIsNotAddressedAsKnown()
    {
        var p = new Ports(); var current = Current(p.Snapshot.Observations[0].Envelope, "private/get_order_history_by_instrument");
        p.Batch = p.Batch with { Observations = new[] { current with { Facts = current.Facts with { ExchangeOrderId = "external" } } } };
        var result = await p.Gate().AssessAsync(Request()); Assert.Contains(DeribitRecoveryEvidenceBlocker.ExternalOrderEvidence, result.Blockers);
        Assert.Equal(1, result.AddressedOrders); // Only the original retained order goes to the known reader.
    }

    [Fact]
    public async Task InvalidRequestAndCancellationDoNoIO()
    {
        var p = new Ports();
        await Assert.ThrowsAsync<ArgumentException>(() => p.Gate().AssessAsync(Request() with { Query = Request().Query with { Epoch = Guid.Empty } }));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Gate().AssessAsync(Request(), stop.Token));
        Assert.Equal(0, p.ReadCalls); Assert.Equal(0, p.QueryCalls); Assert.Equal(0, p.KnownCalls);
    }

    private static AccountKey Foreign() => new(new("foreign"), Account.Venue, Account.Environment, Account.Account);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Ports : IOmsPrivateRecoveryReader, IDeribitRecoveryQueries, IDeribitKnownOrderReader
    {
        internal OmsPrivateRecoveryReadResult Snapshot; internal DeribitRecoveryQueryResult Batch; internal DeribitKnownOrderReadResult Known;
        internal int ReadCalls, QueryCalls, KnownCalls;
        internal Ports(bool expired = false)
        {
            var seed = Seed(expired); var q = Request().Query;
            Snapshot = new(Account, Guid.NewGuid(), Now, 1, new[] { new OmsPrivateRecoveryObservation(seed, OmsPrivateCaptureResult.Inserted) }, Array.Empty<OmsPrivateRecoveryReadProblem>());
            Batch = new(Account, Epoch, q.NativeInstrument, "ETH", q.FromUtc-q.Overlap, q.ThroughUtc, Now, Now,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }.Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(),
                Array.Empty<OmsPrivateEventEnvelope>(), Array.Empty<DeribitRecoveryQueryProblem>());
            Known = new(Account, Epoch, Snapshot.SnapshotId, Now, Now, 1, new[] { new DeribitKnownOrderLookup(0, DeribitKnownOrderLookupOutcome.Found) },
                new[] { Current(seed) }, Array.Empty<DeribitKnownOrderReadProblem>());
        }
        internal DeribitRecoveryEvidenceGate Gate() => new(this, this, this, new FixedClock());
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        { ReadCalls++; return Task.FromResult(Snapshot); }
        public Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery query, CancellationToken ct = default)
        { QueryCalls++; Assert.Equal(Epoch, query.Epoch); return Task.FromResult(Batch); }
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
        { KnownCalls++; Assert.All(request.Orders, x => Assert.Equal("order-1", x.ExchangeOrderId)); return Task.FromResult(Known); }
    }
}
