using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryRunReplayTests
{
    [Fact]
    public void NewDocumentCodecPreservesExistingJournalPrivateFactAndReceiptHashes()
    {
        var command = DeribitCommandReceiptFixtures.Command();
        command = command with { Payload = command.Payload with { Quantity = 1.000m, LimitPrice = 2000.00m } };
        var receipt = DeribitCommandReceiptFixtures.Receipt(command);
        var envelope = DeribitOrderRecoveryProjectorTests.Report() with { Facts = receipt.Order };
        var snapshot = new OmsRecoveryLedgerSnapshot(new(command.Scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 1,
            [new(envelope, OmsPrivateCaptureResult.Inserted)], []),
            [new(command, 1, OmsRecoveryDispatchPhase.Acknowledged, OmsRecoveryOutboxState.Resolved, 1, OmsRecoveryDispatchPhase.Acknowledged)], [], [receipt]);
        var encoded = OmsRecoveryRunCodec.Encode(snapshot);
        var decoded = OmsRecoveryRunCodec.Decode<OmsRecoveryLedgerSnapshot>(encoded.Json, encoded.Sha256);
        Assert.Equal(OmsJournalCanonical.EncodeCommand(command).Sha256, OmsJournalCanonical.EncodeCommand(decoded.Commands[0].Request).Sha256);
        Assert.Equal(OmsPrivateEventCanonical.Encode(envelope).Sha256, OmsPrivateEventCanonical.Encode(decoded.Private.Observations[0].Envelope).Sha256);
        Assert.Equal(DeribitCommandReceiptCanonical.Encode(receipt).Sha256, DeribitCommandReceiptCanonical.Encode(decoded.Receipts[0]).Sha256);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CombinedLedgerReadIsInsideActiveReplayAndInvalidLedgerStopsVenueReads(bool invalid)
    {
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); var ledger = new Ledger(r, invalid);
        var account = new DeribitRecoveryAccountAssessmentTests.Accounts(); var q = r.Request();
        var result = await r.Gate().AssessAccountWithLedgerAndReplayAsync(q, ledger,
            DeribitRecoveryAccountAssessmentTests.Request(q), account, r.Coordinator, r.Session);
        Assert.Equal(1, ledger.Calls); Assert.Equal(0, r.SnapshotCalls);
        Assert.True(result.Account.Replay!.LocalReplayValidated);
        Assert.Equal(1, result.Account.Replay.BufferedObservations);
        Assert.Equal(invalid ? 0 : 1, r.QueryCalls); Assert.Equal(invalid ? 0 : 1, account.Calls);
        Assert.Equal(invalid, result.Ledger.Orders.Blockers.Contains(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid));
        Assert.Equal(result.Ledger.Orders, result.Account.Evidence);
        Assert.Equal(result.Ledger.Orders, result.Account.Replay.Evidence);
        Assert.False(result.AccountReady);
    }
    private sealed class Ledger(DeribitRecoveryReplayGateTests.Rig rig, bool invalid) : IOmsRecoveryLedgerReader
    {
        internal int Calls;
        public async Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        {
            Calls++;
            await rig.Coordinator.TryCaptureReceiptAsync(await rig.Receipt());
            return new(new(scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, [], []), [],
                invalid ? [OmsRecoveryLedgerProblem.InvalidRecord] : []);
        }
    }
}
