using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryAccountAssessmentTests
{
    internal static DeribitRecoveryAccountRequest Request(DeribitRecoveryGateRequest gate, string kind = "future") => new(
        new(gate.Query.Scope, gate.Query.Epoch, "ETH", kind, TimeSpan.FromSeconds(30)),
        new[] { new DeribitAccountInstrumentExpectation("ETH-PERPETUAL", kind == "future" ? "USD" : "ETH", "ETH") });

    internal sealed class Clock : TimeProvider
    {
        internal bool Returned, ExpireAfterReturn; internal Action? AfterReturn; private int stamps;
        public override DateTimeOffset GetUtcNow()
        {
            if (Returned && ++stamps == 2) AfterReturn?.Invoke();
            return DateTimeOffset.UtcNow.AddSeconds(Returned && ExpireAfterReturn && stamps >= 2 ? 31 : 0);
        }
    }
    internal sealed class Accounts(TimeProvider? time = null) : IDeribitAccountSnapshotReader
    {
        internal int Calls; internal Func<CancellationToken, Task>? Hook;
        internal Func<DeribitAccountSnapshotResult, DeribitAccountSnapshotResult>? Transform;
        internal Action? Returned;
        public async Task<DeribitAccountSnapshotResult> ReadAsync(DeribitAccountSnapshotRequest q, CancellationToken ct = default)
        {
            Calls++; if (Hook is not null) await Hook(ct); ct.ThrowIfCancellationRequested();
            var at = (time ?? TimeProvider.System).GetUtcNow();
            var r = new DeribitAccountSnapshotResult(q.Scope, q.Epoch, q.Currency, q.Kind, at, at, q.MaximumObservationSpan,
                new[] { "ETH-PERPETUAL" }, new("ETH", -0.1m, 0.25m, true, true, new(at, at)), new(at, at),
                new[] { new DeribitAccountPosition("ETH-PERPETUAL", q.Kind, "sell", -10, q.Kind == "future" ? "USD" : "ETH",
                    q.Kind == "future" ? -0.005m : null, "ETH") }, Array.Empty<DeribitAccountSnapshotProblem>());
            if (Transform is not null) r = Transform(r);
            Returned?.Invoke(); return r;
        }
    }

    [Theory]
    [InlineData("future")] [InlineData("option")]
    public async Task ActualPortResultIsValidatedWithoutGrantingAccountAuthority(string kind)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var account = new Accounts(); var q = r.Request();
        if (kind == "option") q = q with { AmountUnit = OmsPrivateAmountUnit.BaseCoin, AmountCurrency = "ETH" };
        var result = await r.Gate().AssessAccountAsync(q, Request(q, kind), account);
        Assert.True(result.ScopedAccountObservationsValidated); Assert.Equal(1, result.AcceptedPositionRows); Assert.True(result.BalanceObserved);
        Assert.Null(result.Replay); Assert.Equal(1, account.Calls); Assert.Empty(result.AccountProblems);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers); AssertBlocked(result);
    }

    [Fact]
    public async Task AccountReadIsInsideReplayWindowBeforeSeal()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var account = new Accounts {
            Hook = async _ => await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()) };
        var result = await r.Gate().AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session);
        Assert.True(result.ScopedAccountObservationsValidated); Assert.True(result.Replay!.LocalReplayValidated);
        Assert.Equal(1, result.Replay.BufferedObservations); Assert.Equal(1, account.Calls);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        Assert.Same(result.Evidence, result.Replay.Evidence); AssertBlocked(result);
    }

    [Fact]
    public async Task AccountAgeIsRecheckedAfterReplayDrain()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var clock = new Clock { ExpireAfterReturn = true };
        var account = new Accounts(clock) { Returned = () => clock.Returned = true }; var q = r.Request();
        var gate = new DeribitRecoveryEvidenceGate(r, r, r, clock);
        var result = await gate.AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session);
        Assert.True(result.Replay!.LocalReplayValidated); Assert.Contains(DeribitRecoveryAccountProblem.StaleObservation, result.AccountProblems);
        Assert.False(result.ScopedAccountObservationsValidated); Assert.Equal(0, result.AcceptedPositionRows); Assert.False(result.BalanceObserved);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.AccountObservationStale, result.Evidence.Blockers); AssertBlocked(result);
    }

    [Theory]
    [InlineData("scope", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("epoch", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("currency", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("kind", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("span", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("cached", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("future", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("offset", DeribitRecoveryAccountProblem.BindingInvalid)]
    [InlineData("missing-manifest", DeribitRecoveryAccountProblem.CoverageInvalid)]
    [InlineData("extra-manifest", DeribitRecoveryAccountProblem.CoverageInvalid)]
    [InlineData("duplicate-manifest", DeribitRecoveryAccountProblem.CoverageInvalid)]
    [InlineData("unmapped", DeribitRecoveryAccountProblem.CoverageInvalid)]
    [InlineData("duplicate-position", DeribitRecoveryAccountProblem.CoverageInvalid)]
    [InlineData("unit", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("base", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("position-kind", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("missing-base", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("sign", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("direction", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("balance-currency", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("balance-window", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("position-window", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("overlap", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("null-window", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("null-collections", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("unknown-problem", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("capacity", DeribitRecoveryAccountProblem.ObservationInvalid)]
    [InlineData("failed", DeribitRecoveryAccountProblem.ReadFailed)]
    [InlineData("no-balance", DeribitRecoveryAccountProblem.ReadFailed)]
    public async Task UntrustedOrIncompletePortResultClearsAcceptedObservations(string mode, DeribitRecoveryAccountProblem problem)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var account = new Accounts {
            Transform = x => Change(x, mode) };
        var result = await r.Gate().AssessAccountAsync(q, Request(q), account);
        Assert.False(result.ScopedAccountObservationsValidated); Assert.Equal(0, result.AcceptedPositionRows); Assert.False(result.BalanceObserved);
        Assert.Contains(problem, result.AccountProblems); Assert.Contains(DeribitRecoveryEvidenceBlocker.AccountObservationsUnavailable, result.Evidence.Blockers);
        Assert.Equal(1, account.Calls); AssertBlocked(result);
    }

    private static DeribitAccountSnapshotResult Change(DeribitAccountSnapshotResult x, string mode)
    {
        var p = x.Positions[0]; var at = x.StartedAtUtc;
        return mode switch {
            "scope" => x with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) },
            "epoch" => x with { Epoch = Guid.NewGuid() }, "currency" => x with { Currency = "BTC" }, "kind" => x with { Kind = "option" },
            "span" => x with { MaximumObservationSpan = TimeSpan.FromMinutes(1) }, "cached" => x with { StartedAtUtc = at.AddSeconds(-1) },
            "future" => x with { CompletedAtUtc = at.AddDays(1) }, "offset" => x with { StartedAtUtc = at.ToOffset(TimeSpan.FromHours(2)) },
            "missing-manifest" => x with { RequestedInstruments = Array.Empty<string>() },
            "extra-manifest" => x with { RequestedInstruments = new[] { "ETH-PERPETUAL", "OTHER" } },
            "duplicate-manifest" => x with { RequestedInstruments = new[] { "ETH-PERPETUAL", "ETH-PERPETUAL" } },
            "unmapped" => x with { Positions = new[] { p with { NativeInstrument = "OTHER" } } },
            "duplicate-position" => x with { Positions = new[] { p, p } }, "unit" => x with { Positions = new[] { p with { SizeCurrency = "ETH" } } },
            "base" => x with { Positions = new[] { p with { BaseCurrency = "BTC" } } },
            "position-kind" => x with { Positions = new[] { p with { Kind = "option" } } },
            "missing-base" => x with { Positions = new[] { p with { BaseSize = null } } },
            "sign" => x with { Positions = new[] { p with { BaseSize = 0.005m } } },
            "direction" => x with { Positions = new[] { p with { Direction = "unknown" } } },
            "balance-currency" => x with { Balance = x.Balance! with { Currency = "BTC" } },
            "balance-window" => x with { Balance = x.Balance! with { Window = new(at.AddSeconds(-1), at) } },
            "position-window" => x with { PositionsWindow = new(at, at.AddSeconds(1)) },
            "overlap" => x with { Balance = x.Balance! with { Window = new(at, at.AddMilliseconds(1)) },
                CompletedAtUtc = at.AddMilliseconds(1), PositionsWindow = new(at, at) },
            "null-window" => x with { Balance = x.Balance! with { Window = null! } },
            "null-collections" => x with { Positions = null! },
            "unknown-problem" => x with { Problems = new[] { (DeribitAccountSnapshotProblem)1234 } },
            "capacity" => x with { Positions = Enumerable.Repeat(p, 257).ToArray() },
            "failed" => x with { Problems = new[] { DeribitAccountSnapshotProblem.UnmappedInstrument } },
            _ => x with { Balance = null } };
    }

    [Theory]
    [InlineData("scope")] [InlineData("epoch")] [InlineData("currency")] [InlineData("kind")]
    [InlineData("capacity")] [InlineData("span")] [InlineData("empty")][InlineData("duplicate")]
    [InlineData("missing-selected")][InlineData("unit")]
    public async Task InvalidExpectationsDoNotReadOrReplaceExistingReplayWindow(string mode)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var a = Request(q); var account = new Accounts();
        var window = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        a = mode switch {
            "scope" => a with { Query = a.Query with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) } },
            "epoch" => a with { Query = a.Query with { Epoch = Guid.NewGuid() } }, "currency" => a with { Query = a.Query with { Currency = "BTC" } },
            "kind" => a with { Query = a.Query with { Kind = "spot" } }, "capacity" => a with { Query = a.Query with { MaximumPositions = 257 } },
            "span" => a with { Query = a.Query with { MaximumObservationSpan = TimeSpan.Zero } },
            "empty" => a with { ExpectedInstruments = Array.Empty<DeribitAccountInstrumentExpectation>() },
            "duplicate" => a with { ExpectedInstruments = new[] { a.ExpectedInstruments[0], a.ExpectedInstruments[0] } },
            "missing-selected" => a with { ExpectedInstruments = new[] { a.ExpectedInstruments[0] with { NativeInstrument = "OTHER" } } },
            _ => a with { ExpectedInstruments = new[] { a.ExpectedInstruments[0] with { SizeCurrency = "ETH" } } } };
        await Assert.ThrowsAsync<ArgumentException>(() => r.Gate().AssessAccountWithReplayAsync(q, a, account, r.Coordinator, r.Session));
        Assert.Equal(0, r.SnapshotCalls); Assert.Equal(0, r.QueryCalls); Assert.Equal(0, account.Calls);
        Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(window))).CanReplay);
    }

    [Fact]
    public async Task InvalidRetainedScopeSkipsAccountRead()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); r.SnapshotProblem = true; var account = new Accounts(); var q = r.Request();
        var result = await r.Gate().AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session);
        Assert.Equal(0, account.Calls); Assert.Equal(0, r.QueryCalls); Assert.Contains(DeribitRecoveryAccountProblem.ReadSkipped, result.AccountProblems);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.SnapshotUnusable, result.Evidence.Blockers); AssertBlocked(result);
    }

    [Fact]
    public async Task EmptyScopedPositionsDoNotInventZeros()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var account = new Accounts {
            Transform = x => x with { Positions = Array.Empty<DeribitAccountPosition>() } };
        var result = await r.Gate().AssessAccountAsync(q, Request(q), account);
        Assert.True(result.ScopedAccountObservationsValidated); Assert.Equal(0, result.AcceptedPositionRows); Assert.True(result.BalanceObserved); AssertBlocked(result);
    }

    [Fact]
    public async Task CallerAndPortCollectionsAreFrozenAcrossAwaitedLifecycle()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var expected = Request(q).ExpectedInstruments.ToList();
        var positions = new List<DeribitAccountPosition>(); var names = new List<string> { "ETH-PERPETUAL" }; var clock = new Clock();
        r.QueryHook = _ => { expected.Clear(); return Task.CompletedTask; };
        clock.AfterReturn = () => { positions.Clear(); names.Clear(); };
        var account = new Accounts(clock) { Transform = x => { positions.AddRange(x.Positions); return x with { Positions = positions, RequestedInstruments = names }; },
            Returned = () => clock.Returned = true };
        var result = await new DeribitRecoveryEvidenceGate(r, r, r, clock).AssessAccountWithReplayAsync(q,
            new(Request(q).Query, expected), account, r.Coordinator, r.Session);
        Assert.Empty(expected); Assert.Empty(positions); Assert.Empty(names); Assert.True(result.ScopedAccountObservationsValidated);
        Assert.Equal(1, result.AcceptedPositionRows); AssertBlocked(result);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExceptionOrCancellationInAccountReadPreservesNewerWindow(bool cancel)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); OmsPrivateReplayWindow? newer = null;
        using var stop = new CancellationTokenSource(); var account = new Accounts { Hook = async ct => {
            newer = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
            if (cancel) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new IOException("synthetic private exception"); } };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Gate().AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session, ct: stop.Token));
        else await Assert.ThrowsAsync<IOException>(() => r.Gate().AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session));
        Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(newer!))).CanReplay); Assert.Equal(1, account.Calls);
    }

    [Fact]
    public async Task EpochChangeInsideAccountReadDiscardsAccountAcceptance()
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var q = r.Request(); var account = new Accounts {
            Hook = async _ => await r.Coordinator.BeginEpochAsync(r.Session) };
        var result = await r.Gate().AssessAccountWithReplayAsync(q, Request(q), account, r.Coordinator, r.Session);
        Assert.False(result.Replay!.LocalReplayValidated); Assert.False(result.ScopedAccountObservationsValidated);
        Assert.Contains(DeribitRecoveryAccountProblem.BindingInvalid, result.AccountProblems); AssertBlocked(result);
    }

    internal static void AssertBlocked(DeribitRecoveryAccountAssessment result)
    {
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.AccountSnapshotCoherenceUnproven, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.AccountWideCoverageUnproven, result.Evidence.Blockers);
        Assert.False(result.Evidence.CanRebuildOrders); Assert.False(result.AccountWideCoverage); Assert.False(result.VenueCoherent);
        Assert.False(result.AccountReady); Assert.False(result.DurableProjectionPublished); Assert.False(result.Evidence.AccountReady);
    }
}
